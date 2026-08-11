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
[Collection(PostgresCollectionDefinition.Name)]
public sealed class FeatureCatalogSeedingTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public FeatureCatalogSeedingTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("feature_catalog");
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    private static FeatureCatalogSeeder Seeder(AppDbContext context) =>
        new(context, NullLogger<FeatureCatalogSeeder>.Instance);

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
