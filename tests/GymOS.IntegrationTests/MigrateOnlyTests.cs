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
        //
        // ── Why this asserts a specific SQLSTATE and not just "something threw" ────────────────
        // It used to be `ThrowsAnyAsync<Exception>`, and that assertion held with the entire
        // MigrateOnly branch deleted from Program.cs: the host simply fell through to
        // TenantIsolationValidator, which opened its own connection to the same nonexistent
        // database and threw 3D000 (invalid_catalog_name). The test was green either way, so it
        // pinned nothing about the feature it was written for.
        //
        // 42501 is reachable only through the migrator. It comes from EF's CreateAsync — Migrate
        // finds no history table, asks the database creator to create the database, and the test
        // role holds no CREATEDB — so it can only be raised by something that tried to MIGRATE.
        // Every other path against this connection string stops earlier, at 3D000. Watched failing
        // with the branch removed before being trusted; see the report for that increment.
        PostgresException failure = await ThrowsPostgresExceptionAsync(
            () => MigrateOnlyAsync(builder.ConnectionString));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, failure.SqlState);
    }

    /// <summary>
    /// Runs <paramref name="action"/> and returns the <see cref="PostgresException"/> it produced,
    /// unwrapping whatever the host stack wrapped it in.
    /// </summary>
    /// <remarks>
    /// The exception travels out of <c>WebApplicationFactory</c>'s host build, and neither the number
    /// of wrapper layers nor their types are a property this suite should assert on — but the
    /// SQLSTATE underneath is exactly the property it should. Walking the chain keeps the assertion
    /// on the database's answer rather than on the framework's plumbing. A failure with no
    /// <see cref="PostgresException"/> anywhere in it fails the test with the whole chain in the
    /// message, since "it threw something else entirely" is the interesting case to read.
    /// </remarks>
    private static async Task<PostgresException> ThrowsPostgresExceptionAsync(Func<Task> action)
    {
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(action);

        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        Assert.Fail($"Expected a PostgresException somewhere in the chain, got: {thrown}");
        return null!;
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
