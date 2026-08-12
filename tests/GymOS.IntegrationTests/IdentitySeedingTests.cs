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
                new OutboxSaveChangesInterceptor(clock, new CorrelationContext(), new SystemUser()))
            .Options;
        return new AppDbContext(options);
    }

    private async Task SeedAsync()
    {
        await using AppDbContext context = CreateContext();
        var seeder = new PlatformIdentitySeeder(
            context, new UnitOfWork(context), new PostgresAdvisoryLock(context), NullLogger<PlatformIdentitySeeder>.Instance);
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

        // Compared against the declaration rather than a hard-coded count: a role gaining a
        // permission is a deliberate change to SystemRoles, and this should confirm the seeder
        // reproduced it — not fail merely because the number moved.
        Role supportAgent = Assert.Single(roles, r => r.Name == SystemRoles.SupportAgent);
        Assert.Equal(
            SystemRoles.Definitions
                .Single(definition => definition.Name == SystemRoles.SupportAgent)
                .Permissions
                .Order(StringComparer.Ordinal),
            supportAgent.Permissions
                .Select(grant => permissions.Single(p => p.Id == grant.PermissionId).Key)
                .Order(StringComparer.Ordinal));
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
    public async Task Two_concurrent_seedings_of_a_fresh_database_do_not_race()
    {
        // The identical exposure FeatureCatalogSeedingTests pins for the feature catalogue: two
        // instances booting against a fresh database both see no permissions declared and both try
        // to insert the whole set, one of them racing the permission key's unique index. The
        // advisory lock (same "platform.catalog-seed" name as the feature seeder — both are
        // startup-scope syncs against the same fresh database) makes the second wait, then re-read.
        await using AppDbContext first = CreateContext();
        await using AppDbContext second = CreateContext();

        var seederOne = new PlatformIdentitySeeder(
            first, new UnitOfWork(first), new PostgresAdvisoryLock(first), NullLogger<PlatformIdentitySeeder>.Instance);
        var seederTwo = new PlatformIdentitySeeder(
            second, new UnitOfWork(second), new PostgresAdvisoryLock(second), NullLogger<PlatformIdentitySeeder>.Instance);

        await Task.WhenAll(
            seederOne.SeedAsync(CancellationToken.None),
            seederTwo.SeedAsync(CancellationToken.None));

        await using AppDbContext verify = CreateContext();
        var permissions = await verify.Set<Permission>().ToListAsync();
        Assert.Equal(PlatformPermissions.All.Count, permissions.Count);
        Assert.Equal(
            permissions.Count,
            permissions.Select(permission => permission.Key).Distinct(StringComparer.Ordinal).Count());
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
