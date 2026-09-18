using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MigrateOnlyTests
{
    private readonly SqlServerFixture _sqlServer;

    public MigrateOnlyTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    [Fact]
    public async Task MigrateOnlyAppliesMigrationsAndDoesNotSeed()
    {
        // The migrator's whole job is DDL. If it also seeded, those rows would be written by the role
        // that owns the schema rather than the one that serves requests — and the seeders would run
        // twice in the deployment, once here and once when the API boots.
        // An empty database, and the migrator's credentials — which is the arrangement this mode
        // exists for. In the deployment the `migrator` compose service runs it as `gymos_migrator`
        // against a database nothing has touched; handing it the app role's connection string would
        // fail on `permission denied for schema public` long before it could seed anything, and a
        // pre-migrated database would leave it with nothing to do.
        await using SqlServerFixture.DatabaseLease connectionStringLease = await _sqlServer.LeaseUnmigratedDatabaseAsync("migrate_only");
        string connectionString = connectionStringLease.ConnectionString;

        await MigrateOnlyAsync(SqlServerFixture.AsMigrator(connectionString));

        // Read back as the app role, which is what the API would connect as afterwards.
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
        // A database name nobody has created and this test never creates. EF Core's migrator tries
        // to create it on the fly, and the test principal — in neither dbcreator nor sysadmin, per
        // SqlServerFixture — cannot, so the attempt fails with
        // SqlException 262, "CREATE DATABASE permission denied in database 'master'".
        var builder = new SqlConnectionStringBuilder(_sqlServer.ConnectionString)
        {
            InitialCatalog = "gymos_database_that_was_never_created",
        };

        // The exit code is the load-bearing part of the whole feature: compose gates the API
        // container on this one finishing successfully. Swallowing the failure here (and
        // returning normally) is exactly the bug that would let the API start against an
        // unmigrated database. The surrounding try/catch in Program.cs logs Fatal and rethrows,
        // so the exception must surface here rather than being swallowed silently.
        //
        // ── Why this asserts a specific error number and not just "something threw" ────────────
        // It used to be `ThrowsAnyAsync<Exception>`, and that assertion held with the entire
        // MigrateOnly branch deleted from Program.cs: the host simply fell through to
        // TenantIsolationValidator, which opened its own connection to the same nonexistent
        // database and threw for that instead. The test was green either way, so it pinned nothing
        // about the feature it was written for.
        //
        // 262 is reachable only through the migrator. It comes from EF's CreateAsync — Migrate
        // finds no history table, asks the database creator to create the database, and the test
        // principal cannot — so it can only be raised by something that tried to MIGRATE. Every
        // other path against this connection string stops earlier, at 4060 ("Cannot open database
        // ... requested by the login"), because it never attempts the create at all.
        //
        // Both numbers were confirmed against SQL Server 2022 rather than read from a table: as the
        // unprivileged principal, CREATE DATABASE answers 262 and merely opening the missing
        // database answers 4060. They are the counterparts of the PostgreSQL original's 42501
        // (insufficient_privilege) and 3D000 (invalid_catalog_name), and the distinction between
        // them is the whole assertion.
        SqlException failure = await ThrowsSqlExceptionAsync(
            () => MigrateOnlyAsync(builder.ConnectionString));

        Assert.Equal(CreateDatabasePermissionDenied, failure.Number);
    }

    /// <summary>262 — <c>CREATE DATABASE permission denied in database 'master'</c>.</summary>
    private const int CreateDatabasePermissionDenied = 262;

    /// <summary>
    /// Runs <paramref name="action"/> and returns the <see cref="SqlException"/> it produced,
    /// unwrapping whatever the host stack wrapped it in.
    /// </summary>
    /// <remarks>
    /// The exception travels out of <c>WebApplicationFactory</c>'s host build, and neither the number
    /// of wrapper layers nor their types are a property this suite should assert on — but the error
    /// number underneath is exactly the property it should. Walking the chain keeps the assertion on
    /// the database's answer rather than on the framework's plumbing. A failure with no
    /// <see cref="SqlException"/> anywhere in it fails the test with the whole chain in the message,
    /// since "it threw something else entirely" is the interesting case to read.
    /// </remarks>
    private static async Task<SqlException> ThrowsSqlExceptionAsync(Func<Task> action)
    {
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(action);

        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            if (current is SqlException sql)
            {
                return sql;
            }
        }

        Assert.Fail($"Expected a SqlException somewhere in the chain, got: {thrown}");
        return null!;
    }

    private static AppDbContext CreateContext(string connectionString)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, TestDatabase.Options)
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
