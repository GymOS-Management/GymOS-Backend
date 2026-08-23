using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace GymOS.IntegrationTests.Tenancy;

/// <summary>
/// R14's mechanism, proven rather than installed: a tenant sees only its own rows, a write cannot be
/// forged for another tenant, the table owner does not bypass the policy, platform scope reads
/// nothing rather than everything, an unprotected table fails the boot, platform scope still reads
/// nothing when it borrows the same physical connection a tenant just used, a bypassing or
/// table-owning role fails the boot whether or not anything is tenant-scoped, the role serving
/// requests cannot turn its own isolation off while the migrator can, and the one condition under
/// which the validator still checks nothing is logged rather than passed over in silence.
/// </summary>
/// <remarks>
/// <para><b>The suite's own database, and its own pair of database roles.</b> Every assertion here
/// counts rows, so the suite takes an isolated database. It also takes two dedicated
/// <c>NOSUPERUSER NOBYPASSRLS</c> roles, because the container's bootstrap role is a superuser and
/// PostgreSQL exempts superusers from row-level security unconditionally, <c>FORCE</c> included — run
/// as the bootstrap role, every test below would report "isolation works" no matter what the policies
/// said.</para>
/// <para>The pair mirrors the deployment (<c>docker/postgres/initdb/10-roles.sql</c>) rather than
/// inventing a shape of its own. <see cref="MigratorRole"/> owns the database, the <c>public</c>
/// schema, everything migrations create and the probe table; <see cref="AppRole"/> owns nothing and is
/// what the host, the validator and every isolation test connect as, reaching the migrator's tables
/// only through <c>ALTER DEFAULT PRIVILEGES</c>. That separation is what
/// <see cref="TheAppRoleCannotDisableItsOwnIsolation"/> and
/// <see cref="TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator"/> exist to prove: one
/// statement, refused for the role serving requests and accepted for the role that is not.</para>
/// <para><b><c>Maximum Pool Size=1</c>.</b> Rebuilt here rather than in <c>SqlServerFixture</c>, whose
/// five-connection cap is load-bearing for the rest of the suite. One connection is what makes
/// <see cref="TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection"/> deterministic: the two scopes it
/// opens are guaranteed the same physical connection, so a leaked setting would be observed rather
/// than gambled on. Read that test's own comment before trusting it as a control — Npgsql's pooled
/// connection reset, not the interceptor, is what it currently pins.</para>
/// </remarks>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private static readonly Guid TenantA = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    /// <summary>
    /// The role that owns this suite's database, its schemas and its tables, and applies migrations —
    /// the local stand-in for <c>gymos_migrator</c>. Nothing connects as it to serve a request.
    /// </summary>
    private const string MigratorRole = SqlServerFixture.MigratorPrincipal;

    /// <summary>
    /// The role the host, the validator and every isolation test connect as — the local stand-in for
    /// <c>gymos_app</c>. Not a superuser and not <c>BYPASSRLS</c>, because those attributes defeat
    /// row-level security outright; and the owner of nothing, because an owner can turn <c>FORCE</c>
    /// off on its own tables in one statement.
    /// </summary>
    private const string AppRole = SqlServerFixture.AppPrincipal;

    /// <summary>
    /// The suite no longer builds its own roles, grants or migrations: <c>SqlServerFixture</c> does all
    /// three for every database it hands out, so what this suite used to construct for itself is now
    /// the harness-wide default. A second, differently-named pair here would actively break — the
    /// fixture migrates before returning the connection string, so the tables belong to its migrator
    /// and a local app role would hold no grants over them.
    /// </summary>
    private readonly SqlServerFixture _sqlServer;
    private readonly List<IServiceScope> _scopes = [];
    private readonly List<AppDbContext> _rawContexts = [];
    private string _connectionString = null!;

    /// <summary>
    /// The same database as <see cref="_connectionString"/>, connected as <see cref="MigratorRole"/>:
    /// the owner of the schema, and the role that applies migrations and creates the probe.
    /// </summary>
    private string _migratorConnectionString = null!;

    /// <summary>
    /// The container's bootstrap role, before ownership moves to <see cref="MigratorRole"/> — a
    /// superuser, and the role this suite deliberately avoids connecting the application host as.
    /// Kept so <see cref="CreateSuperuserContext"/> can still reach the database through it after
    /// <see cref="InitializeAsync"/> hands the database and schema away.
    /// </summary>
    private string _bootstrapConnectionString = null!;

    /// <summary>
    /// A database of its own, connected through whatever <see cref="SqlServerFixture.CreateIsolatedDatabaseAsync"/>
    /// hands back — the harness role once Task 1 lands, still the bootstrap superuser until it does.
    /// Kept separate from <see cref="_bootstrapConnectionString"/>'s database so
    /// <see cref="TheHarnessRoleIsNotExemptFromRowLevelSecurity"/> proves something about the fixture's
    /// default connection rather than about a database this suite has already reassigned to
    /// <see cref="MigratorRole"/>.
    /// </summary>
    private string _harnessRoleConnectionString = null!;

    private AuthenticationHost _host = null!;

    public TenantIsolationTests(SqlServerFixture postgres) => _sqlServer = postgres;

    public async Task InitializeAsync()
    {
        _harnessRoleConnectionString = await _sqlServer.CreateIsolatedDatabaseAsync("harness_role");

        // CreateIsolatedDatabaseAsync now hands back a connection as SqlServerFixture's own harness
        // role (Task 1), which has no CREATEROLE — this suite genuinely needs a superuser to create
        // its two roles and hand the database to the migrator, so it takes
        // SqlServerFixture.BootstrapConnectionString and points it at the database
        // CreateIsolatedDatabaseAsync just created, rather than reusing the (now non-superuser)
        // connection string that method returns.
        string appConnectionString = await _sqlServer.CreateIsolatedDatabaseAsync("tenant_isolation");
        string isolatedDatabaseName = new SqlConnectionStringBuilder(appConnectionString).Database!;

        _bootstrapConnectionString = new SqlConnectionStringBuilder(_sqlServer.BootstrapConnectionString)
        {
            Database = isolatedDatabaseName,

            // Unpooled, for the same reason as the migrator string below. This one is used a handful
            // of times per instance — the role script here, and the two tests that want a superuser
            // validator — but xUnit builds a fresh instance per test method against a fresh database,
            // so its pool is never reused and only parks idle connections nothing will claim.
            Pooling = false,
        }.ConnectionString;

        // No role creation here any more. SqlServerFixture builds the migrator/app pair once per
        // container, hands every database it creates to the migrator, applies the default privileges
        // and migrates as the migrator — the shape this suite used to construct for itself. Keeping a
        // second, differently-named pair would now actively break: the fixture migrates before handing
        // the string back, so the tables belong to *its* migrator and a local app role would hold no
        // grants over them.
        _migratorConnectionString = new SqlConnectionStringBuilder(
            SqlServerFixture.AsMigrator(appConnectionString))
        {

            // Unpooled, for the reason SqlServerFixture.Unpooled already gives: xUnit builds a fresh
            // instance of this class per test method, each against a uniquely named database, so this
            // connection string is never reused and a pool behind it only parks idle connections that
            // nothing will claim. The migrator is used a handful of times per test, so the handshake
            // cost is irrelevant.
            //
            // Measured, not predicted. Splitting this suite in two added four test methods, and each
            // method is a fresh instance holding its own pools; with this string and the bootstrap one
            // pooled at the inherited size of five, the run went past `max_connections=300` and failed
            // ten tests with `53300: sorry, too many clients already` in whichever suite happened to be
            // dialing in at the time. Unpooling this one alone left six; unpooling both left none. The
            // same run against HEAD passed 306/306, so this was introduced here rather than inherited.
            Pooling = false,
        }.ConnectionString;

        _connectionString = new SqlConnectionStringBuilder(appConnectionString)
        {
            // One physical connection, so connection reuse is certain rather than lucky (D6). Pooling
            // is asserted explicitly rather than inherited: the fixture's app string is pooled today,
            // but a future change making it unpooled would silently give every scope its own physical
            // connection and quietly defeat
            // TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection — which would still pass, having
            // become a duplicate of PlatformScopeReadsNothing_NotEverything.
            Pooling = true,
            MaxPoolSize = 1,
        }.ConnectionString;

        // Migrations before the host boots, and as the MIGRATOR, for four reasons. They create the
        // `platform` schema the probe table lives in; the app role owns nothing and so could not
        // create it; TenantIsolationValidator returns early while any migration is pending, so a
        // database that has not been migrated would make
        // AProtectedTablePassesValidation_AnUnprotectedOneFailsTheBoot assert on a validator that
        // never looked at anything; and EF compares the model against the migrations snapshot when it
        // migrates, so the host — whose model carries the probe — must not be the one to do it.
        //
        // Everything created here inherits the ALTER DEFAULT PRIVILEGES grants above, which is the
        // only reason the app role can read __EFMigrationsHistory or touch the probe at all.
        // Migrations and the runtime-function grants both moved into SqlServerFixture, which now does
        // them for every database it hands out rather than only for this suite. The reasons they had to
        // happen before the host boots are unchanged and still hold: they create the `platform` schema
        // the probe lives in, the app role owns nothing and could not create it, TenantIsolationValidator
        // returns early while any migration is pending, and EF compares the model against the migrations
        // snapshot — so the host, whose model carries the probe, must not be the one to migrate.

        // Before the host, not after: Program.cs runs the real validator during startup, and the probe
        // is a mapped ITenantScoped entity from this point on. An absent (or unprotected) probe table
        // is exactly the condition the validator kills the boot for — as is one owned by the role the
        // host connects as, which is why this runs as the migrator.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        _host = AuthenticationHost.Create(
            _connectionString,
            settings =>
            {
                // A background job waking up would contend for the single connection this suite allows.
                settings["Jobs:Enabled"] = "false";

                // Already migrated above, deliberately. EF 10 validates the model against the
                // migrations snapshot inside Migrate, and this host's model carries a test-only
                // entity no migration knows about — so letting the host migrate would fail the boot
                // on PendingModelChangesWarning. Nothing is left pending, so the validator's
                // first-boot guard still does not fire.
                settings["Database:MigrateOnStartup"] = "false";
            },
            RegisterTenantProbe);

        // Touching Services boots the host, which runs TenantIsolationValidator over the probe above.
        using IServiceScope boot = _host.Services.CreateScope();
    }

    public async Task DisposeAsync()
    {
        foreach (AppDbContext context in _rawContexts)
        {
            await context.DisposeAsync();
        }

        foreach (IServiceScope scope in _scopes)
        {
            scope.Dispose();
        }

        await _host.DisposeAsync();
    }

    [Fact]
    public async Task ATenantSeesOnlyItsOwnRows()
    {
        await SeedAsync(TenantA, "a-note");
        await SeedAsync(TenantB, "b-note");

        IReadOnlyList<string> notes = await NotesAsAsync(TenantA);

        Assert.Equal(["a-note"], notes);
    }

    [Fact]
    public async Task AWriteCannotBeForgedForAnotherTenant()
    {
        // Acting as A, inserting a row stamped B is refused outright rather than silently landing
        // where the caller cannot see it.
        //
        // The policy's check arm is what refuses it — but note that deleting `WITH CHECK` alone would
        // NOT make this test fail: PostgreSQL falls back to the `USING` expression as the check for
        // INSERT and UPDATE when `WITH CHECK` is absent, and here the two expressions are identical.
        // So this test pins the write path being policed at all; it does not pin which clause polices
        // it. Dropping the policy, or dropping FORCE, is what turns it red.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => SeedAsAsync(actingAs: TenantA, stampedWith: TenantB));
    }

    [Fact]
    public async Task TheOwnerDoesNotBypassThePolicy()
    {
        // The test that justifies FORCE. PostgreSQL exempts a table's owner from its own policies, so
        // WITHOUT `FORCE ROW LEVEL SECURITY` the owner's reads filter nothing.
        //
        // The owner moved when this suite split into a migrator and an app role: the role serving
        // requests now owns nothing, so asserting through it would prove nothing about the risk — the
        // policy constrains a non-owner with or without FORCE. The property has to be observed through
        // the role that IS the owner, which is what the last assertion does.
        //
        // Note where the two guards sit relative to each other, because it is not what it looks like.
        // Deleting FORCE from TenantProbeSchema.CreateSql does NOT reach the assertion below: the
        // validator kills the host during InitializeAsync first, with "row-level security is not
        // FORCED" — run, not assumed. So this assertion is not the tripwire for a probe that ships
        // unforced; it is the tripwire for FORCE being turned off at RUNTIME, on a table that booted
        // correctly, which is exactly what an owner-privileged injected statement would do.
        Assert.True(await TheMigratorOwnsTheProbeTableAsync());

        await SeedAsync(TenantA, "a-note");
        await SeedAsync(TenantB, "b-note");

        Assert.Single(await NotesAsAsync(TenantA));

        // The owner, with no tenant set: FORCE is why this is 0 and not 2. Confirmed by running an
        // `ALTER TABLE ... NO FORCE` as the migrator immediately above this line, which turned it into
        // `Expected: 0, Actual: 2` — so this assertion does detect the condition rather than merely
        // restate it.
        Assert.Equal(0L, await ProbeRowsVisibleToTheOwnerAsync());
    }

    [Fact]
    public async Task PlatformScopeReadsNothing_NotEverything()
    {
        // Fail-closed, asserted rather than assumed. A code path that forgets to set the tenant reads
        // no rows — a visible, empty failure somebody reports — instead of every tenant's rows.
        await SeedAsync(TenantA, "a-note");
        await SeedAsync(TenantB, "b-note");

        Assert.Empty(await NotesWithNoTenantAsync());
    }

    [Fact]
    public async Task AProtectedTablePassesValidation_AnUnprotectedOneFailsTheBoot()
    {
        // The validator's own negative test, running the REAL validator rather than a copy of its
        // query — so relaxing the boot check fails here too.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);
        await ValidatorAsync().ValidateAsync(CancellationToken.None); // does not throw

        await DropProbeAsync();
        await CreateProbeAsync(TenantProbeSchema.CreateWithoutPolicySql);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorAsync().ValidateAsync(CancellationToken.None));

        Assert.Contains("tenant_probe", error.Message, StringComparison.Ordinal);
        Assert.Contains("policy", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARoleThatBypassesRowLevelSecurityFailsTheBoot()
    {
        // FORCE does not constrain a superuser, so a correctly protected table plus a superuser
        // connection is a platform that boots green and serves every tenant's rows to every caller.
        // The table flags D5 reasons about are necessary and not sufficient.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorAsSuperuserAsync().ValidateAsync(CancellationToken.None));

        // "row-level security" would NOT distinguish this from a table failure: InspectAsync's own
        // messages contain that same phrase, so the assertion would pass on a validator that never
        // looked at the role at all. BYPASSRLS appears only in the role message.
        Assert.Contains("BYPASSRLS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HavingNothingToCheckIsLoggedAsAWarning_NotSkippedSilently()
    {
        // "Nothing implements ITenantScoped" and "somebody forgot the marker on Member" are the same
        // code path seen from in here, so it must leave a trace: a boot where the per-table checks
        // inspected nothing has to be distinguishable, afterwards, from a boot where they passed.
        //
        // Read as the APP role, not the bootstrap superuser it used to use. The connected-role check
        // is no longer gated on the model having something tenant-scoped, so a superuser context here
        // now throws rather than logging — which is the whole point of
        // ASuperuserFailsTheBootEvenWithNothingTenantScoped. The assertions below are untouched.
        await DropProbeAsync();

        var logger = new CapturingLogger<TenantIsolationValidator>();
        await new TenantIsolationValidator(CreateRawContext(_connectionString, withTenantProbe: false), logger)
            .ValidateAsync(CancellationToken.None);

        (LogLevel Level, string Message) entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("ITenantScoped", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PendingMigrationsAreLoggedAsAWarning_NotSkippedSilently()
    {
        // The other early return, and the more dangerous one: a deployment with
        // Database:MigrateOnStartup=false and a migration left pending disables R14 entirely on that
        // boot, so the validator must say so rather than return a silent pass.
        //
        // Asked for by name. This used to say "which is what a fresh isolated database is", and that
        // stopped being true the moment SqlServerFixture began migrating each database it hands out —
        // at which point this test quietly started exercising the *other* early return ("checked
        // nothing: no entity implements ITenantScoped") and asserting on its message. It failed
        // loudly, which is the only reason the drift was visible.
        string unmigrated = await _sqlServer.CreateUnmigratedDatabaseAsync("tenant_isolation_pending");

        await using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlServer(unmigrated, TestDatabase.Options)
                .UseSnakeCaseNamingConvention()
                .Options);

        var logger = new CapturingLogger<TenantIsolationValidator>();
        await new TenantIsolationValidator(context, logger).ValidateAsync(CancellationToken.None);

        (LogLevel Level, string Message) entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("pending migrations", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection()
    {
        // What this actually proves, stated honestly: with Maximum Pool Size=1 both scopes below are
        // guaranteed the same PHYSICAL connection, and the second one — platform scope — still reads
        // nothing. That is the fail-closed property (D4) holding end to end across connection reuse.
        //
        // It is NOT a negative control for D6's unconditional write, though it was written believing it
        // was. Changing TenantSessionInterceptor to skip the write when there is no tenant — precisely
        // the shape D6 rejects — leaves this test green, because Npgsql resets a pooled connection on
        // close (DISCARD ALL/RESET ALL) and clears the GUC before this scope ever borrows it. That was
        // run, not reasoned about: all eight tests here passed with the write skipped.
        //
        // So nothing this suite can do makes the unconditional write load-bearing; only setting
        // `No Reset On Close=true` or enabling multiplexing on the connection string would, and both are
        // out of this suite's reach. The write stays as defence in depth against exactly that change,
        // and this test stays for the property in the first paragraph.
        await SeedAsync(TenantA, "a-note");

        IReadOnlyList<string> asA = await NotesAsAsync(TenantA);
        IReadOnlyList<string> asPlatform = await NotesWithNoTenantAsync();

        Assert.Single(asA);
        Assert.Empty(asPlatform);
    }

    [Fact]
    public async Task ASuperuserFailsTheBootEvenWithNothingTenantScoped()
    {
        // The check used to be conditional on something implementing ITenantScoped, purely because the
        // deployment connected as a superuser and an unconditional check would have failed every boot.
        // That is fixed, so the condition goes — which also closes the hole where an entity that forgets
        // the marker silently switches the whole control off.
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorWithNoTenantScopedEntitiesAsSuperuserAsync().ValidateAsync(CancellationToken.None));

        Assert.Contains("BYPASSRLS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenantScopedTableOwnedByTheConnectedRoleFailsTheBoot()
    {
        // The migrator/app split is the reason an injected statement cannot disable isolation. If the
        // connecting role owns the table, it can run ALTER TABLE ... NO FORCE and the split has silently
        // stopped existing — so the boot asserts the separation rather than trusting the deployment.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorOwningTheProbeAsync().ValidateAsync(CancellationToken.None));

        Assert.Contains("owns", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GrantingTheAppRoleMembershipInTheMigratorFailsTheBoot()
    {
        // The ownership check must test effective privilege, not name equality. `GRANT migrator TO app`
        // is a plausible way for a future increment to "fix" a permission error, and nothing in
        // 10-roles.sql forbids it — but it hands the app role the owner's rights, so
        // ALTER TABLE ... NO FORCE comes back within reach while `current_user` still reads as the app
        // role. A check written as `pg_get_userbyid(relowner) = current_user` reports clean through
        // exactly that, which is the green-while-proving-nothing shape this increment exists to remove.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);
        await ExecuteAsync(_bootstrapConnectionString, $"GRANT {MigratorRole} TO {AppRole};");

        try
        {
            // First, that the risk is real rather than theoretical: the statement PostgreSQL refused
            // the app role in TheAppRoleCannotDisableItsOwnIsolation now succeeds, because membership
            // is inherited by default. Restored immediately, so the assertion below is about ownership
            // and not about the flag this line just cleared.
            await ExecuteAsAppRoleAsync("ALTER TABLE platform.tenant_probe NO FORCE ROW LEVEL SECURITY;");
            await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;");

            // And that the validator sees it. `current_user` is still the app role throughout, so only
            // a check written in terms of effective privilege can catch this.
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ValidatorAsync().ValidateAsync(CancellationToken.None));

            Assert.Contains("owns", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // Revoked in a finally, and the stakes went up when the roles moved into SqlServerFixture:
            // they are created once per container and now serve EVERY suite, not just this one. A leaked
            // membership would not merely disarm TheAppRoleCannotDisableItsOwnIsolation — it would make
            // the app role an owner everywhere, so every host booted afterwards would fail
            // TenantIsolationValidator's ownership check and the run would collapse into unrelated
            // failures.
            //
            // Two things keep that from being fragile: this collection is serialised by xUnit, so no
            // other suite is mid-flight while the grant is held, and the revoke runs even if the
            // assertions throw. What it does not survive is the process dying between the two, which is
            // a torn-down container and a fresh one next run.
            await ExecuteAsync(_bootstrapConnectionString, $"REVOKE {MigratorRole} FROM {AppRole};");
        }
    }

    [Fact]
    public async Task TheAppRoleCannotDisableItsOwnIsolation()
    {
        // The reason for two roles rather than one non-superuser role. An owner can turn FORCE off in a
        // single statement, and that statement is within reach of the SQL injection row-level security
        // mostly exists to contain. The app role owns nothing, so PostgreSQL refuses it.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsAppRoleAsync("ALTER TABLE platform.tenant_probe NO FORCE ROW LEVEL SECURITY;"));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator()
    {
        // The control for the test above: it must fail for a privilege reason, not because the statement
        // is malformed, the table is missing, or the connection is broken.
        //
        // The flag is read back rather than letting "no exception" stand as the pass condition. Not
        // throwing is an implicit assertion, and an implicit assertion is one a later `try`/`catch`
        // around ExecuteAsMigratorRoleAsync would neuter in silence — leaving a control that passes
        // unconditionally and therefore certifies nothing about the test it exists to support.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe NO FORCE ROW LEVEL SECURITY;");
        Assert.False(await ProbeIsForcedAsync());

        await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;");
        Assert.True(await ProbeIsForcedAsync());
    }

    [Fact]
    public async Task ATableCreatedByTheMigratorIsUsableByTheAppRoleWithNoExplicitGrant()
    {
        // The piece most likely to be subtly wrong, and its production failure mode is a permission error
        // on a table a future migration has only just added. Nothing here grants anything: the INSERT
        // works only because of the ALTER DEFAULT PRIVILEGES in CreateRolesSql, which were confirmed
        // load-bearing twice over: deleting them from CreateRolesSql fails this test with
        // `42501: permission denied for table __EFMigrationsHistory` before it reaches its body, and
        // an `ALTER DEFAULT PRIVILEGES ... REVOKE ALL ON TABLES` aimed at just this table fails the
        // INSERT below with `42501: permission denied for table default_privilege_probe`.
        await ExecuteAsMigratorRoleAsync(
            "CREATE TABLE platform.default_privilege_probe (id uuid PRIMARY KEY);");

        await ExecuteAsAppRoleAsync(
            $"INSERT INTO platform.default_privilege_probe (id) VALUES ('{Guid.NewGuid()}');");

        await ExecuteAsAppRoleAsync("SELECT count(*) FROM platform.default_privilege_probe;");
    }

    [Fact]
    public async Task TheTwoFunctionsThatIssueDdlOnTheAppRoleSBehalfWorkAndStayContained()
    {
        // The gap the increment's own reviews could not see: taking DDL away from the serving role
        // without auditing what runtime DDL it still performs. Two functions installed by earlier
        // migrations create objects when the application calls them — platform.next_invoice_number
        // creates that year's sequence on the first invoice of a year, and
        // platform.ensure_audit_log_partitions creates next month's audit partition — and both were
        // plain plpgsql, so both ran as the CALLER. As gymos_app, which holds no CREATE on platform,
        // invoicing failed outright with 42501 and partition maintenance failed silently, its
        // `EXCEPTION WHEN others` reporting the privilege error as a routine warning.
        //
        // This is the only harness where any of that is observable. Every other suite connects as a
        // single role that owns everything it migrated, so both calls succeed there whether the fix
        // exists or not — which is how the defect reached a review in the first place.
        //
        // ── Why four properties in one test method ────────────────────────────────────────────
        // Not for brevity. xUnit builds a fresh instance of this class per test method, and each
        // instance provisions its own database, migrates it, and boots a host — this suite's own
        // comments record a previous split into four methods pushing the run past
        // `max_connections=300` and failing ten tests elsewhere with 53300. Splitting these four
        // assertions across four methods did exactly that again (123 failures across the suite);
        // they share one arrangement and one failure mode, so they share an instance.
        //
        // Watched failing before being trusted: with the SecureRuntimeDdlFunctions migration's Up
        // body emptied, the first assertion fails with `42501: permission denied for schema
        // platform` and the catalog assertions fail on prosecdef.

        // 1. The app role can call them at all, and they do the work.
        long invoiceNumber = await ScalarAsAppRoleAsync<long>(
            "SELECT platform.next_invoice_number(2026)");

        Assert.Equal(1, invoiceNumber);

        // A month far enough out that neither the migration's three-month lookahead nor the host's
        // own startup maintenance has already created it — otherwise the function returns 0 for the
        // uninteresting reason that there was nothing to do, and the privilege is never exercised.
        int created = await ScalarAsAppRoleAsync<int>(
            "SELECT platform.ensure_audit_log_partitions(date '2099-01-01', 0)");

        Assert.Equal(1, created);

        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        // 2. SECURITY DEFINER bought the app role the right to CALL them. It must not have handed it
        // ownership of what they produce, or the two-role split would be undone by the very fix that
        // made invoicing work — an owner can ALTER its own table's isolation off. The objects belong
        // to the definer, which is the migrator.
        await using (SqlCommand owner = connection.CreateCommand())
        {
            owner.CommandText =
                """
                SELECT pg_get_userbyid(c.relowner)
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'platform' AND c.relname = 'invoice_number_seq_2026'
                """;

            Assert.Equal(MigratorRole, (string)(await owner.ExecuteScalarAsync())!);
        }

        // 3 and 4. The two properties that make SECURITY DEFINER safe rather than a
        // privilege-escalation vector, read from the catalog rather than from the migration's text.
        // An unpinned search_path lets a caller point the elevated function at a schema it writes
        // and have its own code run as the schema owner. EXECUTE left at the CREATE FUNCTION default
        // — PUBLIC — lends that same elevation to every role in the database. Either alone turns the
        // fix into a worse defect than the one it fixed.
        await using SqlCommand definers = connection.CreateCommand();
        definers.CommandText =
            """
            SELECT p.proname,
                   p.prosecdef,
                   coalesce(array_to_string(p.proconfig, ','), '') LIKE '%search_path=%' AS path_pinned,
                   has_function_privilege('public', p.oid, 'EXECUTE') AS public_may_execute
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'platform'
              AND p.proname IN ('next_invoice_number', 'ensure_audit_log_partitions')
            ORDER BY p.proname
            """;

        var inspected = new List<string>();

        await using (NpgsqlDataReader reader = await definers.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                inspected.Add(name);

                Assert.True(reader.GetBoolean(1), $"{name} is not SECURITY DEFINER.");
                Assert.True(reader.GetBoolean(2), $"{name} does not pin its search_path.");
                Assert.False(reader.GetBoolean(3), $"{name} is executable by PUBLIC.");
            }
        }

        Assert.Equal(["ensure_audit_log_partitions", "next_invoice_number"], inspected);
    }

    [Fact]
    public async Task TheHarnessRoleIsNotExemptFromRowLevelSecurity()
    {
        // The premise every other test in this suite rests on. If the harness silently handed back a
        // superuser connection, all of them would report "isolation works" while filtering nothing —
        // which is precisely how this suite began life before it built a role of its own.
        await using AppDbContext context = CreateHarnessRoleContext();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user";

        await context.Database.OpenConnectionAsync();

        try
        {
            Assert.False((bool)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task TheHarnessRoleOwnsNothingAndHoldsNoDdl()
    {
        // The premise the Members increment rests on, asserted here so it cannot quietly stop being
        // true. `TenantIsolationValidator` builds its table list from the EF model, so the moment any
        // production entity implements ITenantScoped, its ownership check runs in EVERY integration
        // host — not only in suites that read the table. A harness whose role owns the tables it
        // migrated fails that check on every one of them.
        //
        // This suite builds its own migrator/app pair, so it would keep passing on its own while the
        // other three hundred hosts broke. That is why this test interrogates SqlServerFixture's role
        // rather than this suite's: it is the one every other suite actually connects as.
        //
        // Two assertions, because "owns nothing" and "cannot execute DDL" are different claims and the
        // second is the one an attacker cares about. A role can own no tables and still hold CREATE on
        // the schema, which is enough to make one and then own it.
        await using AppDbContext context = CreateHarnessRoleContext();

        // Deliberately asked in terms that hold whether or not migrations have run: an earlier draft
        // used has_schema_privilege(..., 'platform', ...) and died on `3F000 schema does not exist`,
        // which is a test failing for the wrong reason rather than a red phase worth trusting.
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT count(*) FROM pg_class c
                 JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE c.relkind = 'r'
                   AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                   AND pg_get_userbyid(c.relowner) = current_user) AS tables_owned,
                (SELECT pg_get_userbyid(datdba) = current_user
                 FROM pg_database WHERE datname = current_database()) AS owns_database,
                has_database_privilege(current_user, current_database(), 'CREATE') AS can_create
            """;

        await context.Database.OpenConnectionAsync();

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            Assert.Equal(0, reader.GetInt64(0));
            Assert.False(reader.GetBoolean(1));

            // CREATE on the database is what lets a role add a schema and own everything in it, so
            // owning nothing today means little without this.
            Assert.False(reader.GetBoolean(2));
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts <see cref="TenantProbe"/> into the host's EF model, mapped to <c>platform.tenant_probe</c>.
    /// </summary>
    /// <remarks>
    /// <para>Without this the whole suite is theatre: <c>AppDbContext.OnModelCreating</c> scans the two
    /// production assemblies only, so an entity living in the test assembly is never mapped —
    /// <c>TenantIsolationValidator</c> would then enumerate zero tables and its "does not throw" arm
    /// would pass having inspected nothing.</para>
    /// <para>The schema half matters as much. <c>AppDbContext</c> declares no default schema, so a bare
    /// <c>Entity&lt;TenantProbe&gt;()</c> leaves <c>GetSchema()</c> null and the validator probes
    /// <c>public.tenant_probe</c>, which does not exist.</para>
    /// <para><c>ConfigureDbContext</c> adds to the production options rather than replacing them, so the
    /// connection string, the naming convention and — critically — <c>TenantSessionInterceptor</c> stay
    /// exactly as the application registers them. The resolved service type stays
    /// <c>AppDbContext</c>, so nothing in DI or in the validator changes.</para>
    /// </remarks>
    private static void RegisterTenantProbe(IServiceCollection services) =>
        services.ConfigureDbContext<AppDbContext>(options =>
            options.ReplaceService<IModelCustomizer, TenantProbeModelCustomizer>());

    /// <summary>
    /// A context for applying migrations, deliberately without the probe: migrations are generated from
    /// the production model and must not see a test-only entity.
    /// </summary>
    private AppDbContext CreateMigrationContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_migratorConnectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    private async Task SeedAsync(Guid tenant, string note) =>
        await SeedAsAsync(actingAs: tenant, stampedWith: tenant, note);

    private async Task SeedAsAsync(Guid actingAs, Guid stampedWith, string note = "forged") =>
        await InScopeAsync(actingAs, context =>
        {
            context.Add(new TenantProbe { Id = Guid.NewGuid(), TenantId = stampedWith, Note = note });
            return context.SaveChangesAsync();
        });

    private Task<IReadOnlyList<string>> NotesAsAsync(Guid tenant) => NotesAsync(tenant);

    private Task<IReadOnlyList<string>> NotesWithNoTenantAsync() => NotesAsync(tenant: null);

    private Task<IReadOnlyList<string>> NotesAsync(Guid? tenant) =>
        InScopeAsync(tenant, async context =>
        {
            List<string> notes = await context.Set<TenantProbe>()
                .OrderBy(probe => probe.Note)
                .Select(probe => probe.Note)
                .ToListAsync();

            return (IReadOnlyList<string>)notes;
        });

    /// <summary>
    /// One scope, acting for <paramref name="tenant"/> (or for nobody, which is platform scope), with
    /// the context resolved from that scope so <c>TenantSessionInterceptor</c> sees the right value.
    /// </summary>
    private async Task<T> InScopeAsync<T>(Guid? tenant, Func<AppDbContext, Task<T>> work)
    {
        await using AsyncServiceScope scope = _host.Services
            .GetRequiredService<IServiceScopeFactory>()
            .CreateAsyncScope();

        if (tenant is { } acting)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().SetTenant(acting);
        }

        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>
    /// The real validator, from its own scope. Named for how the brief's test body reads it; it hands
    /// back the validator, and <c>ValidateAsync</c> is the awaited part. The scope outlives the call
    /// because the validator holds the context it queries through, so it is disposed with the suite.
    /// </summary>
    private TenantIsolationValidator ValidatorAsync()
    {
        IServiceScope scope = _host.Services.CreateScope();
        _scopes.Add(scope);

        return scope.ServiceProvider.GetRequiredService<TenantIsolationValidator>();
    }

    /// <summary>
    /// A validator over a DbContext connected as the container's bootstrap role — the superuser Task 5
    /// deliberately avoids, because PostgreSQL exempts a superuser (and a BYPASSRLS role) from row-level
    /// security entirely, above whatever the table flags say — with the probe in the model, so there is
    /// a tenant-scoped table for it to have an opinion about.
    /// </summary>
    private TenantIsolationValidator ValidatorAsSuperuserAsync() =>
        new(CreateSuperuserContext(withTenantProbe: true), NullLogger<TenantIsolationValidator>.Instance);

    /// <summary>
    /// A validator over the same superuser connection, but the unmodified <see cref="AppDbContext"/>
    /// model — no <see cref="IModelCustomizer"/> replacement, so nothing implements
    /// <see cref="ITenantScoped"/> and the validator has no subject to check the role against.
    /// </summary>
    private TenantIsolationValidator ValidatorWithNoTenantScopedEntitiesAsSuperuserAsync() =>
        new(CreateSuperuserContext(withTenantProbe: false), NullLogger<TenantIsolationValidator>.Instance);

    /// <summary>
    /// A validator whose connection is <see cref="MigratorRole"/> — the role that created the probe
    /// table and therefore owns it. Not a superuser and not <c>BYPASSRLS</c>, deliberately: the only
    /// thing wrong with this connection is that it owns what it would be serving requests from, so the
    /// ownership check is the only thing that can make it fail.
    /// </summary>
    private TenantIsolationValidator ValidatorOwningTheProbeAsync() =>
        new(CreateMigratorContext(withTenantProbe: true), NullLogger<TenantIsolationValidator>.Instance);

    /// <summary>
    /// A raw <see cref="AppDbContext"/> connected through <see cref="_bootstrapConnectionString"/>
    /// rather than through <c>_host</c>'s DI, so the connected role is the container's bootstrap
    /// superuser instead of <see cref="AppRole"/>.
    /// </summary>
    private AppDbContext CreateSuperuserContext(bool withTenantProbe) =>
        CreateRawContext(_bootstrapConnectionString, withTenantProbe);

    /// <summary>
    /// The same, connected as <see cref="MigratorRole"/> — the role that owns the probe table, which
    /// the validator must refuse to serve requests as.
    /// </summary>
    private AppDbContext CreateMigratorContext(bool withTenantProbe) =>
        CreateRawContext(_migratorConnectionString, withTenantProbe);

    /// <summary>
    /// An <see cref="AppDbContext"/> outside <c>_host</c>'s DI, so the test picks the connected role.
    /// Tracked in <see cref="_rawContexts"/> for disposal, the same way <see cref="ValidatorAsync"/>
    /// tracks its scope.
    /// </summary>
    private AppDbContext CreateRawContext(string connectionString, bool withTenantProbe)
    {
        DbContextOptionsBuilder<AppDbContext> builder = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention();

        if (withTenantProbe)
        {
            builder.ReplaceService<IModelCustomizer, TenantProbeModelCustomizer>();
        }

        var context = new AppDbContext(builder.Options);
        _rawContexts.Add(context);
        return context;
    }

    /// <summary>
    /// A plain <see cref="AppDbContext"/> over <see cref="_harnessRoleConnectionString"/> — whatever
    /// <see cref="SqlServerFixture.CreateIsolatedDatabaseAsync"/> hands back, deliberately neither of
    /// this suite's own roles — so <see cref="TheHarnessRoleIsNotExemptFromRowLevelSecurity"/>
    /// checks the fixture's default connection rather than a role this suite built for itself.
    /// </summary>
    private AppDbContext CreateHarnessRoleContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_harnessRoleConnectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }

    /// <summary>
    /// The probe is DDL, so it is the migrator's to create and drop — the app role owns nothing and
    /// would be refused. This is also what makes the probe migrator-owned, which is the precondition
    /// for <see cref="TheAppRoleCannotDisableItsOwnIsolation"/>.
    /// </summary>
    private Task CreateProbeAsync(string createSql) => ExecuteAsMigratorRoleAsync(createSql);

    private Task DropProbeAsync() => ExecuteAsMigratorRoleAsync(TenantProbeSchema.DropSql);

    private Task ExecuteAsAppRoleAsync(string sql) => ExecuteAsync(_connectionString, sql);

    private Task ExecuteAsMigratorRoleAsync(string sql) => ExecuteAsync(_migratorConnectionString, sql);

    /// <summary>
    /// Whether <see cref="MigratorRole"/> — and not the role the suite serves requests as — owns the
    /// probe table. The precondition that makes <see cref="TheOwnerDoesNotBypassThePolicy"/> a test of
    /// the risk rather than of nothing.
    /// </summary>
    private async Task<bool> TheMigratorOwnsTheProbeTableAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pg_get_userbyid(c.relowner) = current_user
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND c.relname = 'tenant_probe'
            """;

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Whether the probe table currently has <c>FORCE ROW LEVEL SECURITY</c> set, read straight from
    /// <c>pg_class</c> — so a test that changes the flag can assert it actually changed.
    /// </summary>
    private async Task<bool> ProbeIsForcedAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND c.relname = 'tenant_probe'
            """;

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Rows the probe table yields to its own owner with no tenant set. <c>FORCE</c> is the only
    /// reason this is not "all of them".
    /// </summary>
    private async Task<long> ProbeRowsVisibleToTheOwnerAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM platform.tenant_probe";

        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a single-value query on the app-role connection and returns its result.</summary>
    private async Task<T> ScalarAsAppRoleAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Records what was logged, so a test can assert that a control which turned itself off said so.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        Entries.Add((logLevel, formatter(state, exception)));
    }
}

/// <summary>
/// Adds the test-only <see cref="TenantProbe"/> to the model the host's <c>AppDbContext</c> builds,
/// leaving everything the production <c>OnModelCreating</c> does untouched.
/// </summary>
internal sealed class TenantProbeModelCustomizer : RelationalModelCustomizer
{
    public TenantProbeModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        base.Customize(modelBuilder, context);

        modelBuilder.Entity<TenantProbe>(probe =>
        {
            // Explicit schema: the validator reads `GetSchema() ?? "public"`, and AppDbContext sets no
            // default schema, so an unstated schema sends it looking for a table that does not exist.
            probe.ToTable("tenant_probe", "platform");
            probe.HasKey(row => row.Id);

            // Client-assigned, as ApplyClientAssignedKeys does for every production Guid key — that
            // convention ran inside base.Customize, before this entity existed.
            probe.Property(row => row.Id).HasColumnName("id").ValueGeneratedNever();
            probe.Property(row => row.TenantId).HasColumnName("tenant_id");
            probe.Property(row => row.Note).HasColumnName("note");
        });
    }
}
