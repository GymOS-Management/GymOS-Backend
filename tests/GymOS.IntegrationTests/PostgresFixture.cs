using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// One PostgreSQL 17 container shared by the whole integration collection.
/// Suites that need isolation get their own database inside the container via
/// <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>
    /// Each test host gets a small pool, and the server allows more clients than the default.
    ///
    /// <para>Both halves are needed, and the suite outgrew the defaults rather than misusing them. Every
    /// <c>WebApplicationFactory</c> host opens its own Npgsql pool, whose default ceiling is 100
    /// connections — for a suite that boots a fresh host per test class (and a second one whenever it
    /// needs an administrator in a given role), that adds up to far more than PostgreSQL's default
    /// <c>max_connections</c> of 100. The symptom is
    /// <c>53300: sorry, too many clients already</c> arriving in whichever test happens to run when the
    /// limit is reached — so it reads as a flaky failure in unrelated code.</para>
    ///
    /// <para>A five-connection pool is ample: these hosts serve one request at a time.</para>
    /// </summary>
    private const int MaxPoolSizePerHost = 5;

    /// <summary>
    /// Owns each test database, its <c>public</c> schema, and every table migrations create in it.
    /// Runs migrations; nothing else uses it unless a suite genuinely needs DDL.
    /// </summary>
    public const string MigratorRole = "gymos_test_migrator";

    /// <summary>
    /// The role every test host connects as. Owns nothing, holds no DDL, and is neither a superuser
    /// nor <c>BYPASSRLS</c>.
    /// </summary>
    /// <remarks>
    /// <para>Two roles rather than one, mirroring the deployment (<c>docker/postgres/initdb/10-roles.sql</c>),
    /// and the reason is not symmetry for its own sake. <c>TenantIsolationValidator</c> builds its table
    /// list from the <em>EF model</em>, so the moment any production entity implements
    /// <c>ITenantScoped</c> its ownership check runs in <em>every</em> host that builds the model — not
    /// only in suites that read a tenant-scoped table. A harness whose single role owned the tables it
    /// migrated would fail that check on all of them at once, which is why this had to happen before the
    /// first such entity exists rather than alongside it.</para>
    /// <para>The older reason still holds too: Testcontainers' bootstrap role is a superuser, and
    /// PostgreSQL exempts a superuser from row-level security unconditionally — <c>FORCE</c> included.
    /// A suite running as one cannot observe an isolation failure.</para>
    /// </remarks>
    public const string AppRole = "gymos_test_app";

    /// <summary>Passwords for throwaway roles inside a throwaway container; not secrets.</summary>
    private const string MigratorRolePassword = "gymos-test-migrator";

    private const string AppRolePassword = "gymos-test-app";

    /// <summary>
    /// Hands every table, sequence and schema the migrator creates from here on to the app role.
    /// Database-scoped, so it runs once inside each test database, and <em>before</em> migrations —
    /// default privileges do not reach objects that already exist.
    /// </summary>
    /// <remarks>
    /// Deliberately without <c>IN SCHEMA</c>, and deliberately identical to the deployment's
    /// <c>docker/postgres/initdb/10-roles.sql</c>. If one changes, this must change with it: a drift
    /// here keeps the suite green while production breaks, which is the exact failure class the
    /// migrator/app split exists to close.
    /// </remarks>
    private static readonly string GrantToAppRoleSql =
        $"""
        ALTER SCHEMA public OWNER TO "{MigratorRole}";

        ALTER DEFAULT PRIVILEGES FOR ROLE "{MigratorRole}"
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "{AppRole}";
        ALTER DEFAULT PRIVILEGES FOR ROLE "{MigratorRole}"
            GRANT USAGE, SELECT ON SEQUENCES TO "{AppRole}";
        ALTER DEFAULT PRIVILEGES FOR ROLE "{MigratorRole}"
            GRANT USAGE ON SCHEMAS TO "{AppRole}";
        """;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    /// <summary>
    /// The container's bootstrap superuser. For suites that deliberately need one — asserting that a
    /// bypassing role is refused, for instance. Everything else uses <see cref="ConnectionString"/>.
    /// </summary>
    public string BootstrapConnectionString => Pooled(_container.GetConnectionString());

    /// <summary>Connection string of the container's default database, as <see cref="AppRole"/>.</summary>
    public string ConnectionString => AsRole(_container.GetConnectionString(), AppRole, AppRolePassword);

    /// <summary>
    /// The same database as <paramref name="appConnectionString"/>, but connected as the migrator —
    /// for the few suites that legitimately need DDL, such as one creating a probe table.
    /// </summary>
    /// <remarks>
    /// Unpooled: callers use it a handful of times and every test database has a unique name, so a pool
    /// here would only park idle connections nothing will ever claim. See <see cref="Unpooled"/>.
    /// </remarks>
    public static string AsMigrator(string appConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString)
        {
            Username = MigratorRole,
            Password = MigratorRolePassword,
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        string appConnectionString = await CreateUnmigratedDatabaseAsync(databaseName);

        // The fixture migrates, because the role the host connects as no longer can. Hosts used to do
        // this themselves through AuthenticationHost's `Database:MigrateOnStartup`, which is now false
        // for the same reason the deployment's is: DDL belongs to the migrator, and the app role that
        // serves requests owns nothing.
        await MigrateAsync(AsMigrator(appConnectionString));
        await ExecuteAsMigratorAsync(appConnectionString, GrantRuntimeFunctionsSql);

        return appConnectionString;
    }

    /// <summary>
    /// A database with its roles and grants in place but <b>no schema</b> — for the one thing a
    /// migrated database cannot be used to test.
    /// </summary>
    /// <remarks>
    /// <c>TenantIsolationValidator</c> has an early return for pending migrations that switches R14 off
    /// for that boot, and the test proving it logs rather than passing silently needs a genuinely
    /// unmigrated database. That used to be what every fresh database was; now that
    /// <see cref="CreateDatabaseAsync"/> migrates, the state has to be asked for by name — which is the
    /// safer default anyway, since a caller who wanted a working database and silently got an empty one
    /// would see it as a confusing permission or missing-table error much later.
    /// </remarks>
    public Task<string> CreateUnmigratedDatabaseAsync(string prefix) =>
        CreateUnmigratedDatabaseNamedAsync($"{prefix}_{Interlocked.Increment(ref _databaseSequence)}");

    private async Task<string> CreateUnmigratedDatabaseNamedAsync(string databaseName)
    {
        await _container.ExecScriptAsync($"CREATE DATABASE \"{databaseName}\" OWNER \"{MigratorRole}\";");

        // Creating the database with an OWNER is not enough on its own: `public` is owned by
        // pg_database_owner, and EF puts __EFMigrationsHistory there. R14's tenancy suite hit exactly
        // this — migrations fail on their first statement without it. The grants have to be here too,
        // because ALTER DEFAULT PRIVILEGES without IN SCHEMA is database-scoped. All of it must run
        // *inside* the new database, which ExecScriptAsync cannot target, so this opens its own
        // connection.
        //
        // Unpooled, deliberately: every isolated database gets a unique name, so this connection string
        // is never reused and pooling it would only leave an idle connection behind — one per database
        // this method ever creates, for as long as Npgsql's idle lifetime keeps it open. Across a run
        // that creates on the order of a hundred isolated databases, that is enough on its own to reach
        // `max_connections=300` and surface as `53300` in whatever test happens to be dialing in at the
        // time. Closing the socket for real, rather than parking it, is what keeps this addition from
        // being the second contributor the type's own pooling comment already warns about.
        await using (var connection = new NpgsqlConnection(Unpooled(_container.GetConnectionString(), databaseName)))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = GrantToAppRoleSql;
            await command.ExecuteNonQueryAsync();
        }

        return AsRole(_container.GetConnectionString(), AppRole, AppRolePassword, databaseName);
    }

    /// <summary>Runs one statement batch against a database as the migrator, on its own connection.</summary>
    private static async Task ExecuteAsMigratorAsync(string appConnectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(AsMigrator(appConnectionString));
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Lets the app role call the two <c>SECURITY DEFINER</c> functions that issue DDL on the
    /// application's behalf. Runs after migrations, because the functions do not exist until then.
    /// </summary>
    /// <remarks>
    /// The <c>SecureRuntimeDdlFunctions</c> migration revokes the <c>PUBLIC</c> default that
    /// <c>CREATE FUNCTION</c> hands out — a <c>SECURITY DEFINER</c> function executable by
    /// <c>PUBLIC</c> lends its owner's DDL rights to every role in the database — and grants
    /// <c>EXECUTE</c> to <c>gymos_app</c>, the deployment's role name. This harness's app role has a
    /// different name, so it needs the same explicit grant a managed deployment with its own role
    /// names needs (<c>docs/runbooks/managed-postgres-roles.md</c>).
    /// <para>Without it, invoice numbering fails loudly on the first invoice of a year and audit-log
    /// partition maintenance fails quietly on every boot, since that maintainer is non-fatal by
    /// design.</para>
    /// </remarks>
    private static readonly string GrantRuntimeFunctionsSql =
        $"""
        GRANT EXECUTE ON FUNCTION platform.next_invoice_number(integer) TO "{AppRole}";
        GRANT EXECUTE ON FUNCTION platform.ensure_audit_log_partitions(date, integer) TO "{AppRole}";
        """;

    /// <summary>Applies every migration to one database, as the migrator.</summary>
    private static async Task MigrateAsync(string migratorConnectionString)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                migratorConnectionString,
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static string Pooled(string baseConnectionString, string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            MaxPoolSize = MaxPoolSizePerHost,

            // Idle connections are given back quickly, because the cap above bounds each pool and
            // nothing bounds the number of POOLS. Npgsql keys a pool per connection string, every test
            // database has a unique name, and the default idle lifetime is 300 seconds — longer than
            // the whole suite takes. So a suite that finished two minutes ago is still holding up to
            // five sockets it will never use again, and with the fixture now migrating each database
            // the run creates more of them than it used to. That reached `max_connections` and
            // surfaced as `53300: remaining connection slots are reserved for roles with the SUPERUSER
            // attribute` in whichever suite happened to be dialing in — which reads as a failure in
            // unrelated code, exactly like the two connection problems this type's other comments
            // already describe.
            //
            // Fifteen seconds is far longer than any single test needs a connection for and far
            // shorter than a run, so a finished suite's sockets come back while a running one keeps
            // its own. Pruning is what changes here, not the ceiling: MaxPoolSizePerHost stays at five.
            ConnectionIdleLifetime = 15,
            MinPoolSize = 0,
        };

        if (databaseName is not null)
        {
            builder.Database = databaseName;
        }

        return builder.ConnectionString;
    }

    private static string AsRole(
        string baseConnectionString,
        string role,
        string password,
        string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(Pooled(baseConnectionString, databaseName))
        {
            Username = role,
            Password = password,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// A connection string for a single use-and-discard connection: no pool to leave a connection
    /// idling in after <c>DisposeAsync</c>. See the call site in <see cref="CreateDatabaseAsync"/> for
    /// why that distinction matters here specifically.
    /// </summary>
    private static string Unpooled(string baseConnectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = databaseName,
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// A database nobody else will touch, for suites whose assertions depend on the rows
    /// present.
    ///
    /// xUnit constructs a fresh test-class instance per test method, so a fixed database name
    /// silently shares state across every method in the class — and a test that counts rows
    /// then passes or fails on execution order. The suffix makes the isolation real rather
    /// than assumed.
    /// </summary>
    public Task<string> CreateIsolatedDatabaseAsync(string prefix) =>
        CreateDatabaseAsync($"{prefix}_{Interlocked.Increment(ref _databaseSequence)}");

    private int _databaseSequence;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Once per container. Both ALTERs run here, not through a second connection, because
        // ExecScriptAsync targets the container's default database, which is the one they need to be
        // inside.
        //
        // Both are needed, not just the schema one: `ALTER SCHEMA public OWNER TO` alone (which is all
        // CreateDatabaseAsync needs for a database it *creates* `OWNER "{TestRole}"`) leaves this
        // pre-existing default database itself owned by the bootstrap superuser. The very first
        // migration calls EnsureSchema to create the `platform` schema, and CREATE SCHEMA needs
        // database-level CREATE — owning `public` does not confer that. Verified by running the whole
        // suite without this line: every host that boots against the default database (ApiEndpointTests,
        // for one) failed migrating with `42501: permission denied for database`.
        await _container.ExecScriptAsync(
            $"""
            CREATE ROLE "{MigratorRole}" LOGIN PASSWORD '{MigratorRolePassword}' NOSUPERUSER NOBYPASSRLS;
            CREATE ROLE "{AppRole}"      LOGIN PASSWORD '{AppRolePassword}'      NOSUPERUSER NOBYPASSRLS;

            DO $$
            BEGIN
                EXECUTE format('ALTER DATABASE %I OWNER TO %I', current_database(), '{MigratorRole}');
            END
            $$;

            {GrantToAppRoleSql}
            """);

        // The container's own default database gets the same treatment as every created one, because
        // suites do connect to it (ApiEndpointTests, for one) and it is not created by
        // CreateDatabaseAsync.
        await MigrateAsync(AsMigrator(ConnectionString));
        await ExecuteAsMigratorAsync(ConnectionString, GrantRuntimeFunctionsSql);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
