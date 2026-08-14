using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class MigrateOnlyTests
{
    private readonly PostgresFixture _postgres;

    public MigrateOnlyTests(PostgresFixture postgres) => _postgres = postgres;

    [Fact]
    public async Task MigrateOnlyAppliesMigrationsAndDoesNotSeed()
    {
        // The migrator's whole job is DDL. If it also seeded, those rows would be written by the role
        // that owns the schema rather than the one that serves requests — and the seeders would run
        // twice in the deployment, once here and once when the API boots.
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("migrate_only");

        await MigrateOnlyAsync(connectionString);

        await using AppDbContext context = CreateContext(connectionString);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());

        // `Permission` and `Role` are what PlatformIdentitySeeder writes
        // (PlatformIdentitySeeder.cs:74 and :106). Both empty means the schema exists and nothing
        // seeded it — which is the whole distinction this test draws.
        Assert.False(await context.Set<Permission>().AnyAsync());
        Assert.False(await context.Set<Role>().AnyAsync());
    }

    [Fact]
    public async Task MigrateOnlyAgainstAnUnreachableDatabase_DoesNotExitZero()
    {
        // A database name nobody has created and this test never creates. EF Core's migrator
        // tries to create it on the fly, and the test role — non-superuser, per PostgresFixture —
        // has no CREATEDB privilege, so the very first statement fails:
        // Npgsql.PostgresException 42501, "permission denied to create database".
        var builder = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
        {
            Database = "gymos_database_that_was_never_created",
        };

        // The exit code is the load-bearing part of the whole feature: compose gates the API
        // container on this one finishing successfully. Swallowing the failure here (and
        // returning normally) is exactly the bug that would let the API start against an
        // unmigrated database. The surrounding try/catch in Program.cs logs Fatal and rethrows,
        // so the exception must surface here rather than being swallowed silently.
        await Assert.ThrowsAnyAsync<Exception>(() => MigrateOnlyAsync(builder.ConnectionString));
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    private static async Task MigrateOnlyAsync(string connectionString)
    {
        await using AuthenticationHost host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Database:MigrateOnStartup"] = "false";
            settings["Database:MigrateOnly"] = "true";
        });

        // Accessing Services forces WebApplicationFactory to build and boot the host, which is
        // where the MigrateOnly branch runs.
        _ = host.Services;
    }
}
