using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
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
    /// <para>Mixed-case with a digit and a symbol, because <c>CREATE LOGIN</c> runs with
    /// <c>CHECK_POLICY = ON</c> to match the deployment. The PostgreSQL fixture's
    /// <c>gymos-test-migrator</c> would be rejected outright.</para>
    /// <para><b>And deliberately unrelated to the principal names.</b> SQL Server's policy also
    /// refuses any password containing the login name, and it reports that refusal with the same
    /// "not complex enough" message it uses for everything else — so the obvious
    /// <c>Gymos_test_migrator1</c>, which is complex by every stated criterion, fails with an error
    /// that names the wrong reason. Every database in the suite is created by this fixture, so that
    /// one rejection failed all 303 integration tests at once, in the fixture's constructor, with a
    /// message pointing at character classes.</para>
    /// </remarks>
    private const string MigratorPassword = "Tc7-Sch3ma-Owner!";

    private const string AppPassword = "Tc7-R3quest-Serving!";

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

        -- Metadata visibility, and nothing else. SQL Server's catalog views are permission-filtered,
        -- and a security policy is an object the app principal holds no permission on — so without
        -- this, `sys.security_predicates` reads as empty on the application's connection and
        -- TenantIsolationValidator refuses to boot every host in the suite, reporting a policy that
        -- demonstrably exists as missing. See docker/sqlserver/initdb/10-principals.sql.
        GRANT VIEW DEFINITION ON SCHEMA::platform TO [{AppPrincipal}];
        """;

    private readonly MsSqlContainer _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest@sha256:ba4c8329f48fb8f02e1416be6a930ebfd71268caee78aa985f3af4315e457c89")
        // Feature parity with the deployment and with Azure SQL. Testcontainers' default is
        // Developer edition already, but stating it means a change to that default cannot silently
        // move the suite onto Express — where the 10 GB cap and the missing features would surface
        // as unrelated-looking failures in the partitioning and isolation suites.
        .WithEnvironment("MSSQL_PID", "Developer")
        // Must match what 00-database.sql creates, or every temp-table join in a test throws a
        // collation conflict against tempdb.
        .WithEnvironment("MSSQL_COLLATION", "Latin1_General_100_CI_AS")
        // Bounds the buffer pool. Left alone, SQL Server sizes it against the whole machine and
        // grows into everything available — which on a developer laptop is memory the test host, the
        // build and Docker itself are also using, and on a CI runner is most of the box. Neither
        // survives it well: the engine stops answering under pressure and the run fails with
        // transport-level errors in whichever test was mid-query. PostgreSQL's defaults are modest
        // enough that its fixture never had to say anything about memory.
        .WithEnvironment("MSSQL_MEMORY_LIMIT_MB", "3072")
        // Two readiness conditions in place of the module's own. Testcontainers.MsSql declares the
        // container ready when `sqlcmd -Q "SELECT 1"` succeeds — and the engine answers that as soon
        // as it is listening, which is BEFORE it has finished starting: model and msdb are still
        // recovering, and a background task is still expanding tempdb to one data file per core,
        // holding schema-modification locks on master's catalog while it does. The fixture's first
        // statement is `ALTER DATABASE [model]`, which queues behind that task on a Sch-S lock
        // (`sys.dm_exec_requests` during the window: our ALTER suspended on LCK_M_SCH_S, blocked by
        // a TASK MANAGER session holding LCK_M_SCH_M in master). On a laptop the queue is ~3 seconds;
        // on the 2026-09-03 GitHub runners it outlasted SqlClient's 30-second command timeout,
        // InitializeAsync threw, and xUnit failed all 312 tests of the collection at [1 ms] each
        // without running one. The green run an hour earlier had simply won the race.
        //
        // `WithWaitStrategy` REPLACES the module's probe rather than adding to it — verified: its
        // `sqlcmd` executions no longer appear in the Testcontainers log. Nothing is lost. The two
        // conditions run in this order, and the second cannot pass without a successful connection to
        // master over the mapped port, which is the stronger form of what the probe checked.
        .WithWaitStrategy(Wait.ForUnixContainer()
            // The errorlog's own "start-up done" line. It covers master, model and msdb recovery —
            // but not the tempdb expansion, which the engine logs *after* it (observed 0.3 s to
            // 5 s later), hence the second condition.
            .UntilMessageIsLogged("Recovery is complete", w => w.WithTimeout(StartupTimeout))
            .AddCustomWaitStrategy(new UntilStartupTasksHaveFinished(), w => w.WithTimeout(StartupTimeout)))
        .Build();

    /// <summary>
    /// How long each of the two start-up conditions above may take before the fixture gives up.
    /// Generous on purpose: what this guards against is the engine being slow to finish starting
    /// on a contended runner disk, and a wait that gives up at the first sign of slowness would
    /// reproduce the failure it exists to prevent. Testcontainers reports the expiry as a
    /// <see cref="TimeoutException"/> naming the strategy — a far better error than 312 identical
    /// "Execution Timeout Expired" failures.
    /// </summary>
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Ready when SQL Server's start-up work has actually finished — not merely when it listens.
    /// </summary>
    /// <remarks>
    /// <para>Two facts, read as <c>sa</c> on <c>master</c>. First, tempdb has as many data files as
    /// start-up will give it: SQL Server configures one per logical processor, capped at eight
    /// (observed on this image: 16 cores → 8 files, 4 → 4, 1 → 1), and the task doing that
    /// expansion is the one holding the catalog locks the fixture's DDL then waits on. On a single
    /// core there is nothing to expand and the condition holds at once. Second, no system session
    /// still holds a schema-modification lock in <c>master</c> — the general form of the same
    /// contention, so a start-up task this fixture has not met yet is waited out rather than
    /// collided with.</para>
    /// <para>A probe that cannot get its answer says "not yet" rather than failing the wait: the
    /// command timeout is short so a probe blocked by the very lock it is looking for comes back
    /// and is retried, and a connection refused mid-start is the same answer.</para>
    /// </remarks>
    private sealed class UntilStartupTasksHaveFinished : IWaitUntil
    {
        private const string StartupTasksHaveFinishedSql = """
            SELECT CASE
                WHEN (SELECT COUNT(*) FROM tempdb.sys.database_files WHERE type = 0)
                     >= (SELECT IIF(cpu_count > 8, 8, cpu_count) FROM sys.dm_os_sys_info)
                 AND NOT EXISTS (
                     SELECT 1
                     FROM sys.dm_tran_locks AS l
                     JOIN sys.dm_exec_sessions AS s ON s.session_id = l.request_session_id
                     WHERE l.resource_database_id = DB_ID('master')
                       AND l.request_mode = 'Sch-M'
                       AND s.is_user_process = 0)
                THEN 1
                ELSE 0
            END;
            """;

        public async Task<bool> UntilAsync(IContainer container)
        {
            try
            {
                await using var connection = new SqlConnection(
                    Unpooled(((MsSqlContainer)container).GetConnectionString(), "master"));
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = StartupTasksHaveFinishedSql;
                command.CommandTimeout = 5;

                return await command.ExecuteScalarAsync() is 1;
            }
            catch (SqlException)
            {
                return false;
            }
        }
    }

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

    /// <remarks>
    /// <paramref name="prefix"/>, not a name: the sequence number
    /// <see cref="CreateUnmigratedDatabaseAsync"/> appends is what keeps two instances of the same
    /// test class apart. xUnit builds a fresh instance per test method, so a suite calling this with
    /// a fixed string would ask for the same database once per method and every call after the first
    /// would fail with "database already exists" — which is exactly what happened when this method
    /// briefly bypassed the counter.
    /// </remarks>
    public async Task<string> CreateDatabaseAsync(string prefix)
    {
        string appConnectionString = await CreateUnmigratedDatabaseAsync(prefix);

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
        // The suite may never name an environment database. It already cannot reach one — every
        // database here is created inside a throwaway Testcontainers instance, so `GymOS_Production`
        // asked for from a test would be a new empty database in a container, not the real thing.
        //
        // The assertion is still worth its line, because that reasoning depends on the container and
        // the container is one refactor away from being an environment variable. `DatabaseEnvironment-
        // Guard` stops a *deployed* host opening the wrong database; this stops the test suite ever
        // being the thing that names one, so the two together leave no path in. It throws rather than
        // sanitising: a test that wants a managed name has a bug in the test, and quietly renaming it
        // would hide that.
        if (DatabaseEnvironmentGuard.IsManaged(databaseName))
        {
            throw new InvalidOperationException(
                $"The integration suite must never create or open '{databaseName}': it is one of the "
                    + "three environment databases (GymOS_Local, GymOS_UAT, GymOS_Production). Tests use "
                    + "generated per-test database names inside the Testcontainers instance.");
        }

        // CREATE DATABASE cannot run inside a multi-statement batch that also uses it, and it must
        // not run in a transaction — hence its own round trip against master.
        //
        // The collation is restated per database rather than inherited from the instance. It is the
        // one property of the schema that no migration can assert and every text comparison depends
        // on: SearchPatterns relies on LIKE being case-insensitive, which is the collation's doing
        // and nothing else's.
        // Explicit, small files, and they are load-bearing rather than tidy.
        //
        // A suite run creates on the order of three hundred databases in one container — one per test
        // method for every class that asks in InitializeAsync, which is nearly all of them (2026-09-18:
        // 322 tests, ~280 databases). Each class gives its databases back through DropDatabaseAsync
        // when it is done, so the container holds one class's worth at a time rather than the whole
        // run's — the run's worth filled a GitHub runner's disk, twice. SQL Server's
        // default for a new one is inherited from `model` — an 8 MB data file and an 8 MB log that
        // grow in 64 MB steps. Three hundred of those is gigabytes of files the run never fills, and the
        // I/O of allocating them is enough to make the engine stop answering: the symptom is a
        // transport-level "existing connection was forcibly closed" in whichever test happened to be
        // mid-query, which reads as flakiness in unrelated code.
        //
        // The PostgreSQL fixture needed none of this — CREATE DATABASE there clones a template
        // directory and costs almost nothing — so this is a genuinely new obligation rather than a
        // setting that was overlooked before. 16 MB is more than any single suite writes; the growth
        // step is small so a suite that somehow exceeds it still runs.
        //
        // Recovery model is NOT set here. It is set once on `model` in InitializeAsync so every
        // database inherits it — see there for why a hundred ALTER DATABASE statements is the wrong
        // way to get the same result.
        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), "master"),
            $"""
            CREATE DATABASE [{databaseName}]
                ON PRIMARY (NAME = N'{databaseName}_data', FILENAME = '/var/opt/mssql/data/{databaseName}.mdf',
                            SIZE = 16MB, FILEGROWTH = 16MB)
                LOG ON     (NAME = N'{databaseName}_log',  FILENAME = '/var/opt/mssql/data/{databaseName}_log.ldf',
                            SIZE = 16MB, FILEGROWTH = 16MB)
                COLLATE Latin1_General_100_CI_AS;

            -- Row versioning for READ COMMITTED, matching docker/sqlserver/initdb/00-database.sql and
            -- Azure SQL's own default. Without it a reader takes shared locks, and two handlers that
            -- never contended under PostgreSQL's MVCC deadlock through a table neither of them locked
            -- on purpose — see that file for the specific pair, and the test that catches it.
            --
            -- Safe to ALTER here despite needing exclusive access: the database was created one
            -- statement ago and nothing has connected to it yet.
            ALTER DATABASE [{databaseName}] SET READ_COMMITTED_SNAPSHOT ON;
            """);

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

    /// <summary>SQL Server's "was chosen as the deadlock victim" error.</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>
    /// Command timeout for the fixture's own DDL, in seconds. SqlClient's default of 30 is a budget
    /// for a query, not for a <c>CREATE DATABASE</c> allocating files on a contended runner disk or
    /// an <c>ALTER DATABASE</c> queued behind the engine's own start-up work. The wait strategies on
    /// the container builder keep the second case from arising; this keeps a slow first case — or a
    /// start-up task those strategies have not met yet — reading as a slow statement rather than as
    /// every test in the collection failing at once. Two minutes still fails a genuinely stuck
    /// statement well inside the CI job's 25-minute budget.
    /// </summary>
    private const int DdlCommandTimeoutSeconds = 120;

    /// <summary>Runs one statement batch on its own connection.</summary>
    /// <remarks>
    /// A deadlock is printed and then rethrown, deliberately in that order and with no retry. An
    /// earlier attempt to retry 1205 here was reverted: the deadlock still won after five attempts,
    /// and because SQL Server's deadlock monitor runs on a roughly five-second cycle, each attempt
    /// cost seconds merely to be detected — turning a 45-second failure into a 25-minute job
    /// timeout that reported nothing at all. Failing fast is the better of the two failure modes;
    /// what it was missing was evidence, which is what <see cref="DumpDeadlockGraphsAsync"/> adds.
    /// </remarks>
    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = DdlCommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException ex) when (ex.Number == DeadlockVictim)
        {
            await DumpDeadlockGraphsAsync(connectionString);
            throw;
        }
    }

    /// <summary>Prints every deadlock graph the instance has recorded. Never throws.</summary>
    /// <remarks>
    /// <para><b>This exists because the counterparty is unknown.</b> The integration suite has
    /// deadlocked on GitHub runners in <c>InitializeAsync</c> and in <c>CreateDatabaseAsync</c>,
    /// while passing on developer machines. It was assumed to be the instance settling after
    /// start-up; that assumption did not survive a run still deadlocking twenty-three minutes in.
    /// Two fixes were attempted against a cause nobody had named, and the second made things worse.
    /// So this names it instead of guessing again.</para>
    ///
    /// <para>No extended-events session has to be created: SQL Server captures
    /// <c>xml_deadlock_report</c> into the always-on <c>system_health</c> session by default, and
    /// the ring buffer still holds it after the victim's exception has surfaced. The graph names
    /// both parties, their statements and the resource they contended over — which is exactly what
    /// every guess so far has lacked.</para>
    ///
    /// <para>Every failure here is swallowed and printed. A diagnostic that can throw would replace
    /// the failure being diagnosed, and the deadlock is the more important of the two. If the
    /// connection lacks <c>VIEW SERVER STATE</c> the message says so rather than disappearing.</para>
    /// </remarks>
    private static async Task DumpDeadlockGraphsAsync(string connectionString)
    {
        const string RingBufferQuery = """
            SELECT CONVERT(nvarchar(max), graph.query('.')) AS deadlock_graph
            FROM (
                SELECT CONVERT(xml, target_data) AS target_data
                FROM sys.dm_xe_session_targets AS st
                JOIN sys.dm_xe_sessions AS s ON s.address = st.event_session_address
                WHERE s.name = 'system_health' AND st.target_name = 'ring_buffer'
            ) AS ring
            CROSS APPLY ring.target_data.nodes('RingBufferTarget/event[@name="xml_deadlock_report"]') AS graphs(graph);
            """;

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = RingBufferQuery;

            await using var reader = await command.ExecuteReaderAsync();

            int found = 0;
            while (await reader.ReadAsync())
            {
                Console.WriteLine($"===== deadlock graph {++found} =====");
                Console.WriteLine(reader.IsDBNull(0) ? "(null)" : reader.GetString(0));
            }

            Console.WriteLine(found == 0
                ? "===== no xml_deadlock_report found in system_health's ring buffer ====="
                : $"===== {found} deadlock graph(s) above =====");
        }
        catch (Exception diagnostic)
        {
            Console.WriteLine($"===== could not read the deadlock graph: {diagnostic.Message} =====");
        }
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

    /// <summary>
    /// Gives a database back: drops it, and its files with it. Every test class that asks this fixture
    /// for a database calls this from <c>DisposeAsync</c>; method-scoped databases go through
    /// <see cref="LeaseDatabaseAsync"/> instead, which calls it on dispose.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> A run creates on the order of three hundred databases, 32 MB each
    /// by construction, in the container's writable layer — and until 2026-09-18 dropped none of them,
    /// because nothing needed it to: a database that is never read again costs nothing but disk. Then
    /// the disk ran out. A GitHub runner leaves ~11 GB free after the build, the suite's databases
    /// alone are ~9 GB, and the last two classes failed on <c>CREATE DATABASE</c> with
    /// <c>MODIFY FILE … operating system error 31</c>, twice, at the same point. Dropping at the end of
    /// each class keeps the container at one class's worth of databases instead of the run's.</para>
    ///
    /// <para><b>The fixture drops because the fixture cannot decide.</b> It never learns when a class
    /// finishes, and <c>TenantIsolationTests</c> holds two databases through one <c>InitializeAsync</c>,
    /// so no heuristic here — "drop what the previous prefix created", "drop all but the newest" —
    /// is safe. Ownership stays with the class; this is the primitive it calls.</para>
    ///
    /// <para><b>Pooled connections are the thing that would make this hang.</b> A disposed
    /// <c>WebApplicationFactory</c> leaves its SqlClient pool behind for the life of the process, and
    /// a plain <c>DROP DATABASE</c> waits for every connection to close. So this process's pool for
    /// the database is cleared first, and the database is put in <c>SINGLE_USER WITH ROLLBACK
    /// IMMEDIATE</c> before the drop, which kills whatever is still attached server-side. The pooled
    /// connections that were cleared are never asked for again — every database name is unique —
    /// so nothing observes them going away.</para>
    ///
    /// <para><b>Idempotent, and the shared database is refused.</b> <c>DisposeAsync</c> runs on
    /// failure too, and sometimes twice; a second drop finding nothing is not an error, because a
    /// cleanup that can fail replaces the failure being cleaned up. The shared database — the one
    /// every suite that does not ask for isolation connects to — is refused by name rather than
    /// trusted to callers: a class that dropped it would fail every class after it, with a message
    /// about a missing database that has nothing to do with the class that removed it.</para>
    /// </remarks>
    public async Task DropDatabaseAsync(string connectionString)
    {
        string databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        string sharedDatabaseName = new SqlConnectionStringBuilder(ConnectionString).InitialCatalog;

        if (string.IsNullOrWhiteSpace(databaseName)
            || string.Equals(databaseName, sharedDatabaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(databaseName, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to drop '{databaseName}': it is the shared database every non-isolated suite "
                    + "connects to (or a system database). Only databases handed out by "
                    + "CreateDatabaseAsync, CreateIsolatedDatabaseAsync or CreateUnmigratedDatabaseAsync "
                    + "may be dropped.");
        }

        using (var pooledTemplate = new SqlConnection(connectionString))
        {
            SqlConnection.ClearPool(pooledTemplate);
        }

        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), "master"),
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """);
    }

    /// <summary>
    /// A migrated, isolated database that drops itself when disposed — for tests that create a database
    /// inside the test method rather than in <c>InitializeAsync</c>, where an <c>await using</c> at the
    /// declaration is the whole of the cleanup.
    /// </summary>
    public async Task<DatabaseLease> LeaseDatabaseAsync(string prefix) =>
        new(this, await CreateDatabaseAsync(prefix));

    /// <summary>
    /// <see cref="LeaseDatabaseAsync"/> without the migrations — see
    /// <see cref="CreateUnmigratedDatabaseAsync"/> for the one thing that needs it.
    /// </summary>
    public async Task<DatabaseLease> LeaseUnmigratedDatabaseAsync(string prefix) =>
        new(this, await CreateUnmigratedDatabaseAsync(prefix));

    /// <summary>
    /// A database held for the length of an <c>await using</c>. Disposing it drops the database
    /// through <see cref="DropDatabaseAsync"/>, with that method's guarantees.
    /// </summary>
    public sealed class DatabaseLease : IAsyncDisposable
    {
        private readonly SqlServerFixture _fixture;

        internal DatabaseLease(SqlServerFixture fixture, string connectionString)
        {
            _fixture = fixture;
            ConnectionString = connectionString;
        }

        /// <summary>Connection string of the leased database, as <see cref="AppPrincipal"/>.</summary>
        public string ConnectionString { get; }

        public ValueTask DisposeAsync() => new(_fixture.DropDatabaseAsync(ConnectionString));
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Every database CREATE DATABASE makes is copied from `model`, so setting the recovery model
        // there once is how all hundred of them get it.
        //
        // The obvious alternative — `ALTER DATABASE … SET RECOVERY SIMPLE` alongside each CREATE —
        // was tried and is actively worse. ALTER DATABASE takes a database-level lock, and running
        // one per creation against an instance that is simultaneously serving other suites deadlocks:
        // "Transaction was deadlocked on lock resources … ALTER DATABASE statement failed", in
        // whichever test happened to be starting up. One statement here, before any test runs,
        // has no such contention.
        //
        // SIMPLE because nothing in a test container is ever recovered to a point in time, and FULL
        // means the log grows until someone backs it up — which nothing here does.
        await ExecuteAsync(
            Unpooled(_container.GetConnectionString(), "master"),
            "ALTER DATABASE [model] SET RECOVERY SIMPLE;");

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
