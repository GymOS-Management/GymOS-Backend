using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// One SQL Server 2022 container shared by the whole integration collection.
/// Suites that need isolation get their own database inside the container via
/// <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Each test host gets a small pool.
    ///
    /// <para>The suite outgrew the default rather than misusing it. Every <c>WebApplicationFactory</c>
    /// host opens its own SqlClient pool, whose default ceiling is 100 connections — for a suite that
    /// boots a fresh host per test class (and a second one whenever it needs an administrator in a
    /// given principal), that adds up to far more than any server wants to hold open. Five is ample:
    /// these hosts serve one request at a time.</para>
    ///
    /// <para>SQL Server has no <c>max_connections</c> to raise in reply, which is the one thing that
    /// changed here from the PostgreSQL fixture. Its limit is 32,767 and effectively never reached; a
    /// runaway pool count shows up as memory pressure and slow logins rather than the
    /// <c>53300: sorry, too many clients already</c> that used to surface as a flaky failure in
    /// whichever unrelated test happened to be dialing in. Capping each pool is still the right thing
    /// to do — it just no longer has a server-side counterpart.</para>
    /// </summary>
    private const int MaxPoolSizePerHost = 5;

    /// <summary>
    /// Owns each test database's schemas and every object migrations create in it. Runs migrations;
    /// nothing else uses it unless a suite genuinely needs DDL.
    /// </summary>
    public const string MigratorPrincipal = "gymos_test_migrator";

    /// <summary>
    /// The principal every test host connects as. Owns nothing, holds no DDL rights, and is a member
    /// of no role that could disable a security policy.
    /// </summary>
    /// <remarks>
    /// <para>Two principals rather than one, mirroring the deployment
    /// (<c>docker/sqlserver/initdb/10-principals.sql</c>), and the reason is not symmetry for its own
    /// sake. <c>TenantIsolationValidator</c> builds its table list from the <em>EF model</em>, so the
    /// moment any production entity implements <c>ITenantScoped</c> its per-table check runs in
    /// <em>every</em> host that builds the model — not only in suites that read a tenant-scoped table.
    /// A harness whose single principal could ALTER the tables it migrated would fail that check on
    /// all of them at once.</para>
    /// <para>The connected-principal half of the same validator makes it unconditional: it refuses to
    /// boot a host whose principal is <c>sysadmin</c>, <c>db_owner</c>, <c>db_ddladmin</c>,
    /// <c>db_securityadmin</c>, or holds <c>ALTER ANY SECURITY POLICY</c> or <c>CONTROL</c>. The
    /// container's bootstrap login is <c>sa</c> and is every one of those, so a suite running as it
    /// could not boot a host at all — let alone observe an isolation failure.</para>
    /// </remarks>
    public const string AppPrincipal = "gymos_test_app";

    /// <summary>
    /// Passwords for throwaway principals inside a throwaway container; not secrets.
    /// </summary>
    /// <remarks>
    /// Mixed-case with a digit because <c>CREATE LOGIN</c> runs with <c>CHECK_POLICY = ON</c>, matching
    /// the deployment. The PostgreSQL fixture's <c>gymos-test-migrator</c> would be rejected outright.
    /// </remarks>
    private const string MigratorPassword = "Gymos_test_migrator1";

    private const string AppPassword = "Gymos_test_app1";

    /// <summary>
    /// Creates a database's principals, schemas, and standing grants — everything that must exist
    /// <em>before</em> migrations run.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately the same shape as the deployment's
    /// <c>docker/sqlserver/initdb/10-principals.sql</c>. If one changes, this must change with it: a
    /// drift here keeps the suite green while production breaks, which is the exact failure class the
    /// migrator/app split exists to close.</para>
    /// <para>The schemas are created here rather than left to EF's <c>EnsureSchema</c> for the two
    /// reasons the deployment script gives at length — the migrator must OWN them, because the runtime
    /// modules are <c>EXECUTE AS OWNER</c> and resolve the owner from the schema; and
    /// <c>GRANT EXECUTE ON SCHEMA::platform</c> has to name a schema that already exists. That grant
    /// is this harness's counterpart of the PostgreSQL fixture's <c>ALTER DEFAULT PRIVILEGES</c>
    /// block, and it is what replaces <c>GrantRuntimeFunctionsSql</c>: there is no longer a separate
    /// post-migration grant step, because a standing schema grant already covers every module the
    /// migration adds.</para>
    /// </remarks>
    private static readonly string BootstrapDatabaseSql =
        $"""
        CREATE USER [{MigratorPrincipal}] FOR LOGIN [{MigratorPrincipal}];
        CREATE USER [{AppPrincipal}]      FOR LOGIN [{AppPrincipal}];

        ALTER ROLE db_ddladmin   ADD MEMBER [{MigratorPrincipal}];
        ALTER ROLE db_datareader ADD MEMBER [{MigratorPrincipal}];
        ALTER ROLE db_datawriter ADD MEMBER [{MigratorPrincipal}];
        GRANT ALTER ANY SECURITY POLICY TO [{MigratorPrincipal}];

        ALTER ROLE db_datareader ADD MEMBER [{AppPrincipal}];
        ALTER ROLE db_datawriter ADD MEMBER [{AppPrincipal}];

        EXEC('CREATE SCHEMA platform AUTHORIZATION [{MigratorPrincipal}]');
        EXEC('CREATE SCHEMA outbox   AUTHORIZATION [{MigratorPrincipal}]');

        GRANT EXECUTE ON SCHEMA::platform TO [{AppPrincipal}];
        """;

    private readonly MsSqlContainer _container = new MsSqlBuilder()
        .WithImage("mcr.microsoft.com/mssql/server:2022-latest")
        // Feature parity with the deployment and with Azure SQL. Testcontainers' default is
        // Developer edition already, but stating it means a change to that default cannot silently
        // move the suite onto Express — where the 10 GB cap and the missing features would surface
        // as unrelated-looking failures in the partitioning and isolation suites.
        .WithEnvironment("MSSQL_PID", "Developer")
        // Must match what 00-database.sql creates, or every temp-table join in a test throws a
        // collation conflict against tempdb.
        .WithEnvironment("MSSQL_COLLATION", "Latin1_General_100_CI_AS")
        .Build();

    /// <summary>
    /// The container's bootstrap <c>sa</c> login. For suites that deliberately need a privileged
    /// principal — asserting that one is refused, for instance. Everything else uses
    /// <see cref="ConnectionString"/>.
    /// </summary>
    public string BootstrapConnectionString => Pooled(_container.GetConnectionString());

    /// <summary>Connection string of the shared default database, as <see cref="AppPrincipal"/>.</summary>
    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// The same database as <paramref name="appConnectionString"/>, but connected as the migrator —
    /// for the few suites that legitimately need DDL, such as one creating a probe table.
    /// </summary>
    /// <remarks>
    /// Unpooled: callers use it a handful of times and every test database has a unique name, so a
    /// pool here would only park idle connections nothing will ever claim. See <see cref="Unpooled"/>.
    /// </remarks>
    public static string AsMigrator(string appConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(appConnectionString)
        {
            UserID = MigratorPrincipal,
            Password = MigratorPassword,
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        string appConnectionString = await CreateUnmigratedDatabaseNamedAsync(databaseName);

        // The fixture migrates, because the principal the host connects as no longer can. Hosts used
        // to do this themselves through AuthenticationHost's `Database:MigrateOnStartup`, which is
        // false for the same reason the deployment's is: DDL belongs to the migrator, and the
        // principal that serves requests owns nothing.
        await MigrateAsync(AsMigrator(appConnectionString));

        return appConnectionString;
    }

    /// <summary>
    /// A database with its principals and grants in place but <b>no schema objects</b> — for the one
    /// thing a migrated database cannot be used to test.
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
        // CREATE DATABASE cannot run inside a multi-statement batch that also uses it, and it must
        // not run in a transaction — hence its own round trip against master.
        //
        // The collation is restated per database rather than inherited from the instance. It is the
        // one property of the schema that no migration can assert and every text comparison depends
        // on: SearchPatterns relies on LIKE being case-insensitive, which is the collation's doing
        // and nothing else's.
        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), "master"),
            $"CREATE DATABASE [{databaseName}] COLLATE Latin1_General_100_CI_AS;");

        // All of this must run *inside* the new database, so it opens its own connection.
        //
        // Unpooled, deliberately: every isolated database gets a unique name, so this connection
        // string is never reused and pooling it would only leave an idle connection behind — one per
        // database this method ever creates, for as long as the pool's idle lifetime keeps it open.
        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), databaseName),
            BootstrapDatabaseSql);

        return AsAppPrincipal(_container.GetConnectionString(), databaseName);
    }

    /// <summary>Runs one statement batch on its own connection.</summary>
    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Applies every migration to one database, as the migrator.</summary>
    /// <remarks>
    /// The provider options must match <c>AddPlatformPersistence</c> exactly — in particular the
    /// migrations history table's schema. A harness that left it in <c>dbo</c> would migrate a
    /// database the host under test then reports as having every migration pending, and R14's
    /// validator would take its skip path in every single test.
    /// </remarks>
    private static async Task MigrateAsync(string migratorConnectionString)
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                migratorConnectionString,
                sqlServer => sqlServer
                    .MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!)
                    .MigrationsHistoryTable(
                        PlatformInfrastructureServiceCollectionExtensions.HistoryTableName,
                        PlatformInfrastructureServiceCollectionExtensions.PlatformSchema))
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var context = new AppDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static string Pooled(string baseConnectionString, string? databaseName = null)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            MaxPoolSize = MaxPoolSizePerHost,
            MinPoolSize = 0,

            // The container's certificate is self-signed. Testcontainers' own connection string
            // already sets this; restated because every string this class builds passes through
            // SqlConnectionStringBuilder and a future change to that default would otherwise break
            // every suite at once with a certificate error.
            TrustServerCertificate = true,

            // UnitOfWork recovers a violated constraint's name by parsing the server's message,
            // because SqlException exposes it nowhere else, and SQL Server localises that message.
            // GymFeatureApiTests asserts the retry that depends on it.
            CurrentLanguage = "us_english",
        };

        if (databaseName is not null)
        {
            builder.InitialCatalog = databaseName;
        }

        return builder.ConnectionString;
    }

    private static string AsAppPrincipal(string baseConnectionString, string? databaseName = null)
    {
        var builder = new SqlConnectionStringBuilder(Pooled(baseConnectionString, databaseName))
        {
            UserID = AppPrincipal,
            Password = AppPassword,
        };

        return builder.ConnectionString;
    }

    /// <summary>
    /// A connection string for a single use-and-discard connection: no pool to leave a connection
    /// idling in after <c>DisposeAsync</c>. See the call sites in
    /// <see cref="CreateUnmigratedDatabaseNamedAsync"/> for why that distinction matters here.
    /// </summary>
    public static string Unpooled(string baseConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = databaseName,
            Pooling = false,
            TrustServerCertificate = true,
            CurrentLanguage = "us_english",
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

        // Logins are server-scoped, so they are created once per container rather than per database.
        // Their database users are created per database by BootstrapDatabaseSql, which is the same
        // split the deployment has between logins and contained users.
        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), "master"),
            $"""
            CREATE LOGIN [{MigratorPrincipal}] WITH PASSWORD = '{MigratorPassword}', CHECK_POLICY = ON;
            CREATE LOGIN [{AppPrincipal}]      WITH PASSWORD = '{AppPassword}',      CHECK_POLICY = ON;
            """);

        // The shared database every suite that does not ask for isolation connects to. The
        // PostgreSQL fixture reused the container's own default database for this; SQL Server's
        // equivalent would be `master`, which must not hold application tables, so the shared
        // database is created like any other instead.
        ConnectionString = await CreateDatabaseAsync("gymos_shared");
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollectionDefinition : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
