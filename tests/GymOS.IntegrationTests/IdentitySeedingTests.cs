using GymOS.BuildingBlocks.Infrastructure.Context;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdentitySeedingTests : IAsyncLifetime
{
    private static readonly DateTimeOffset KnownInstant = new(2026, 8, 4, 16, 30, 0, TimeSpan.Zero);

    private readonly PostgresFixture _postgres;
    private string _connectionString = string.Empty;

    public IdentitySeedingTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateDatabaseAsync("seeding_tests");
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        var clock = new FakeClock(KnownInstant);
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new AuditableTimestampsInterceptor(clock),
                new OutboxSaveChangesInterceptor(clock, new CorrelationContext()))
            .Options;
        return new AppDbContext(options);
    }

    private async Task SeedAsync()
    {
        await using AppDbContext context = CreateContext();
        var seeder = new PlatformIdentitySeeder(context, NullLogger<PlatformIdentitySeeder>.Instance);
        await seeder.SeedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Seeding_CreatesTheFullCatalog_AndIsIdempotent()
    {
        await SeedAsync();
        await SeedAsync(); // second run must change nothing

        await using AppDbContext context = CreateContext();

        var permissions = await context.Set<Permission>().ToListAsync();
        Assert.Equal(PlatformPermissions.All.Count, permissions.Count);
        Assert.All(permissions, p => Assert.Equal(KnownInstant, p.CreatedAtUtc)); // timestamps interceptor worked

        var roles = await context.Set<Role>().Include(r => r.Permissions).ToListAsync();
        Assert.Equal(SystemRoles.Definitions.Count, roles.Count);
        Assert.All(roles, r => Assert.True(r.IsSystem));

        Role superAdmin = Assert.Single(roles, r => r.Name == SystemRoles.SuperAdmin);
        Assert.Equal(permissions.Count, superAdmin.Permissions.Count); // grants everything

        Role supportAgent = Assert.Single(roles, r => r.Name == SystemRoles.SupportAgent);
        Assert.Equal(2, supportAgent.Permissions.Count);
    }

    [Fact]
    public async Task Seeding_PublishesDomainEventsThroughTheOutbox()
    {
        await SeedAsync();

        await using AppDbContext context = CreateContext();
        var eventNames = await context.Set<OutboxMessage>()
            .Select(message => message.EventName)
            .Distinct()
            .ToListAsync();

        Assert.Contains("gymos.platform.role.created", eventNames);
        Assert.Contains("gymos.platform.role.permission_granted", eventNames);
    }

    [Fact]
    public async Task Seeding_ReconcilesSystemRoleGrants_BackToTheCanonicalSet()
    {
        await SeedAsync();

        // Drift: someone grants ReadOnly a manage permission out of band.
        await using (AppDbContext context = CreateContext())
        {
            Role readOnly = await context.Set<Role>()
                .Include(r => r.Permissions)
                .SingleAsync(r => r.Name == SystemRoles.ReadOnly);
            Permission manage = await context.Set<Permission>()
                .SingleAsync(p => p.Key == PlatformPermissions.UsersManage);
            readOnly.GrantPermission(manage.Id);
            await context.SaveChangesAsync();
        }

        await SeedAsync(); // reconciliation

        await using (AppDbContext context = CreateContext())
        {
            Role readOnly = await context.Set<Role>()
                .Include(r => r.Permissions)
                .SingleAsync(r => r.Name == SystemRoles.ReadOnly);
            Permission manage = await context.Set<Permission>()
                .SingleAsync(p => p.Key == PlatformPermissions.UsersManage);

            Assert.DoesNotContain(readOnly.Permissions, grant => grant.PermissionId == manage.Id);
        }
    }
}
