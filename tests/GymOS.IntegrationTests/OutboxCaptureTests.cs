using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Context;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Events;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The first end-to-end proof of AD-13: aggregates raise events, the interceptor
/// writes outbox rows in the same transaction, rollback leaves nothing behind.
/// Uses a test-only aggregate because no production aggregate exists until M1.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class OutboxCaptureTests : IAsyncLifetime
{
    private sealed record TestGymRenamed(Guid GymId, string NewName) : DomainEvent
    {
        public override string EventName => "gymos.integrationtests.test_gym.renamed";
    }

    private sealed class TestGym : AggregateRoot
    {
        public TestGym(Guid id, string name)
            : base(id)
            => Name = name;

        private TestGym()
        {
        }

        public string Name { get; private set; } = string.Empty;

        public void Rename(string newName)
        {
            Name = newName;
            Raise(new TestGymRenamed(Id, newName));
        }
    }

    private sealed class IntegrationDbContext : DbContext
    {
        public IntegrationDbContext(DbContextOptions<IntegrationDbContext> options)
            : base(options)
        {
        }

        protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => configurationBuilder.ApplyGymOSConventions();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyConfigurationsFromAssembly(typeof(OutboxMessage).Assembly);
            modelBuilder.Entity<TestGym>(entity =>
            {
                entity.ToTable("test_gyms", "integration_tests");
                entity.HasKey(gym => gym.Id);
                entity.Property(gym => gym.Name).HasMaxLength(200);
            });
        }
    }

    private static readonly DateTimeOffset KnownInstant = new(2026, 8, 4, 15, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = string.Empty;

    public OutboxCaptureTests(SqlServerFixture postgres) => _sqlServer = postgres;

    public async Task InitializeAsync()
    {
        // Unmigrated, deliberately. This suite builds its own tiny schema through EnsureCreated rather
        // than using the application's, and EnsureCreated does nothing at all when the database already
        // contains tables — so against a migrated database it silently created no `integration_tests`
        // schema and every test failed later with `42P01: relation does not exist`.
        _connectionString = await _sqlServer.CreateUnmigratedDatabaseAsync("outbox_tests");

        // EnsureCreated is DDL, so it runs as the migrator: the app role this suite's tests connect as
        // owns nothing and holds no CREATE. What it creates lands under the migrator, which is what
        // makes the ALTER DEFAULT PRIVILEGES grants apply to it — the same arrangement the deployment
        // uses, where migrations create and the API only reads and writes.
        await using (IntegrationDbContext migrator =
            CreateContext(new CorrelationContext(), SqlServerFixture.AsMigrator(_connectionString)))
        {
            await migrator.Database.EnsureCreatedAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IntegrationDbContext CreateContext(
        CorrelationContext correlationContext,
        string? connectionString = null)
    {
        var interceptor = new OutboxSaveChangesInterceptor(
            new FakeClock(KnownInstant), correlationContext, new SystemUser());
        DbContextOptions<IntegrationDbContext> options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseSqlServer(connectionString ?? _connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor)
            .Options;
        return new IntegrationDbContext(options);
    }

    [Fact]
    public async Task SavingAnAggregate_WritesTheOutboxMessage_WithFullEnvelope()
    {
        var correlationContext = new CorrelationContext();
        var correlationId = Guid.NewGuid();
        correlationContext.Set(correlationId);

        var gym = new TestGym(Guid.NewGuid(), "Old Name");

        await using (IntegrationDbContext context = CreateContext(correlationContext))
        {
            context.Add(gym);
            gym.Rename("Iron Temple");
            await context.SaveChangesAsync();
        }

        Assert.Empty(gym.DomainEvents); // drained by the interceptor

        await using (IntegrationDbContext context = CreateContext(new CorrelationContext()))
        {
            OutboxMessage message = Assert.Single(await context.Set<OutboxMessage>().ToListAsync());

            Assert.Equal("gymos.integrationtests.test_gym.renamed", message.EventName);
            Assert.Equal(1, message.EventVersion);
            Assert.Equal(nameof(TestGym), message.AggregateType);
            Assert.Equal(gym.Id, message.AggregateId);
            Assert.Equal(correlationId, message.CorrelationId);
            Assert.Equal(KnownInstant, message.OccurredAtUtc);
            Assert.Equal(0, message.CausationDepth);
            Assert.Null(message.TenantId);

            using JsonDocument payload = JsonDocument.Parse(message.Payload);
            Assert.Equal("Iron Temple", payload.RootElement.GetProperty("newName").GetString());
            Assert.Equal(gym.Id, payload.RootElement.GetProperty("gymId").GetGuid());
        }
    }

    [Fact]
    public async Task RolledBackTransaction_LeavesNoOutboxMessage_NoGhostEvents()
    {
        var gym = new TestGym(Guid.NewGuid(), "Phantom Gym");

        await using (IntegrationDbContext context = CreateContext(new CorrelationContext()))
        {
            await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync();
            context.Add(gym);
            gym.Rename("Never Happened");
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using (IntegrationDbContext context = CreateContext(new CorrelationContext()))
        {
            Assert.False(await context.Set<OutboxMessage>()
                .AnyAsync(message => message.AggregateId == gym.Id));
        }
    }

    [Fact]
    public async Task MultipleEventsFromOneAggregate_AllCaptured_EachWithUniqueEventId()
    {
        var gym = new TestGym(Guid.NewGuid(), "Start");

        await using (IntegrationDbContext context = CreateContext(new CorrelationContext()))
        {
            context.Add(gym);
            gym.Rename("First");
            gym.Rename("Second");
            await context.SaveChangesAsync();
        }

        await using (IntegrationDbContext context = CreateContext(new CorrelationContext()))
        {
            var messages = await context.Set<OutboxMessage>()
                .Where(message => message.AggregateId == gym.Id)
                .ToListAsync();

            Assert.Equal(2, messages.Count);
            Assert.Distinct(messages.Select(message => message.EventId));
        }
    }
}
