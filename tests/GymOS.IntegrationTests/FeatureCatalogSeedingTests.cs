using GymOS.Platform.Domain.Features;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The catalogue seeder against real PostgreSQL — and above all the property that makes it
/// different from the permission seeder: <b>it never overwrites what an operator changed</b>.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class FeatureCatalogSeedingTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public FeatureCatalogSeedingTests(SqlServerFixture postgres) => _sqlServer = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("feature_catalog");
        // Migrations moved into SqlServerFixture, which applies them as the migrator before handing
        // back a connection string: the app role every host connects as owns nothing and holds no DDL.
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    private static FeatureCatalogSeeder Seeder(AppDbContext context) =>
        new(context, new UnitOfWork(context), new SqlServerAdvisoryLock(context), NullLogger<FeatureCatalogSeeder>.Instance);

    [Fact]
    public async Task The_first_run_inserts_every_declared_feature()
    {
        await using AppDbContext context = CreateContext();

        await Seeder(context).SeedAsync(CancellationToken.None);

        List<Feature> features = await context.Set<Feature>().AsNoTracking().ToListAsync();

        Assert.Equal(PlatformFeatures.All.Count, features.Count);
        Assert.Equal(
            PlatformFeatures.All.Select(definition => definition.Key).Order(),
            features.Select(feature => feature.Key).Order());

        // The declaration's opening values land as written.
        Feature pos = features.Single(feature => feature.Key == PlatformFeatures.Pos);
        Assert.True(pos.DefaultEnabled);
        Assert.False(pos.IsPremium);
        Assert.Equal("Point of sale", pos.Name);
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await using AppDbContext first = CreateContext();
        await Seeder(first).SeedAsync(CancellationToken.None);

        await using AppDbContext second = CreateContext();
        await Seeder(second).SeedAsync(CancellationToken.None);

        // Idempotent, and the unique index on `key` would have refused a duplicate anyway.
        Assert.Equal(
            PlatformFeatures.All.Count,
            await second.Set<Feature>().CountAsync());
    }

    [Fact]
    public async Task An_operator_edit_survives_the_next_startup()
    {
        // THE test for this class. §3.5 lets an operator change a feature's default and copy; a
        // seeder that reconciled on boot would revert that silently, and the edit would appear to
        // work until someone restarted the process — the worst shape a bug can take.
        await using (AppDbContext seed = CreateContext())
        {
            await Seeder(seed).SeedAsync(CancellationToken.None);
        }

        await using (AppDbContext edit = CreateContext())
        {
            Feature pos = await edit.Set<Feature>()
                .SingleAsync(feature => feature.Key == PlatformFeatures.Pos);

            // Declared: enabled, not premium, named "Point of sale".
            pos.SetDefault(false);
            pos.SetPremium(true);
            pos.Describe("Till", "Renamed by an operator.", "Front desk");
            await edit.SaveChangesAsync();
        }

        await using (AppDbContext restart = CreateContext())
        {
            await Seeder(restart).SeedAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        Feature after = await verify.Set<Feature>()
            .AsNoTracking()
            .SingleAsync(feature => feature.Key == PlatformFeatures.Pos);

        Assert.False(after.DefaultEnabled);
        Assert.True(after.IsPremium);
        Assert.Equal("Till", after.Name);
        Assert.Equal("Renamed by an operator.", after.Description);
        Assert.Equal("Front desk", after.Category);
    }

    [Fact]
    public async Task A_newly_declared_key_is_inserted_without_disturbing_the_others()
    {
        // Simulates the catalogue growing in a later release: seed, delete one row to stand in for
        // "this key was declared after the database was created", edit another, then re-run.
        await using (AppDbContext seed = CreateContext())
        {
            await Seeder(seed).SeedAsync(CancellationToken.None);
        }

        await using (AppDbContext prepare = CreateContext())
        {
            Feature toRemove = await prepare.Set<Feature>()
                .SingleAsync(feature => feature.Key == PlatformFeatures.Marketing);
            prepare.Remove(toRemove);

            Feature edited = await prepare.Set<Feature>()
                .SingleAsync(feature => feature.Key == PlatformFeatures.Crm);
            edited.SetDefault(true); // declared false

            await prepare.SaveChangesAsync();
        }

        await using (AppDbContext rerun = CreateContext())
        {
            await Seeder(rerun).SeedAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();

        // The missing key is back...
        Assert.True(await verify.Set<Feature>().AnyAsync(f => f.Key == PlatformFeatures.Marketing));

        // ...and the edited one is still the operator's.
        Feature crm = await verify.Set<Feature>()
            .AsNoTracking()
            .SingleAsync(feature => feature.Key == PlatformFeatures.Crm);
        Assert.True(crm.DefaultEnabled);
    }

    [Fact]
    public async Task Two_concurrent_seedings_of_a_fresh_database_do_not_race()
    {
        // The defect this task fixes: two instances booting against a fresh database both read an
        // empty catalogue and both try to insert the same declared rows. Without serialising them,
        // one loses to ux_features_key with a raw DbUpdateException that used to escape SeedAsync
        // uncaught (it called SaveChangesAsync directly, not through IUnitOfWork) and take the host
        // down with it. The advisory lock now makes the second instance wait for the first's
        // transaction to commit, then re-read the catalogue and find nothing left to insert.
        await using AppDbContext first = CreateContext();
        await using AppDbContext second = CreateContext();

        await Task.WhenAll(
            Seeder(first).SeedAsync(CancellationToken.None),
            Seeder(second).SeedAsync(CancellationToken.None));

        await using AppDbContext verify = CreateContext();
        List<Feature> features = await verify.Set<Feature>().AsNoTracking().ToListAsync();

        Assert.Equal(PlatformFeatures.All.Count, features.Count);
        Assert.Equal(
            features.Count,
            features.Select(feature => feature.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task An_undeclared_row_is_left_alone_rather_than_deleted()
    {
        await using (AppDbContext seed = CreateContext())
        {
            await Seeder(seed).SeedAsync(CancellationToken.None);

            // A key from a future (or reverted) release. Deleting it would cascade into the plan
            // contents and gym overrides that reference it, so the seeder only warns.
            seed.Add(Feature.FromDefinition(new FeatureDefinition(
                "retired_thing", "Retired thing", "No longer declared.", "Operations", false, false)));
            await seed.SaveChangesAsync();
        }

        await using (AppDbContext rerun = CreateContext())
        {
            await Seeder(rerun).SeedAsync(CancellationToken.None);
        }

        await using AppDbContext verify = CreateContext();
        Assert.True(await verify.Set<Feature>().AnyAsync(feature => feature.Key == "retired_thing"));
    }
}
