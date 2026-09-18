using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Tenancy;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// <para><b>The suite's own database, and its own pair of database principals.</b> Every assertion
/// here counts rows, so the suite takes an isolated database. It also uses the fixture's two
/// unprivileged principals rather than the container's <c>sa</c> login, because <c>sa</c> holds
/// <c>ALTER ANY SECURITY POLICY</c> — a connection that can switch the policy off is not one these
/// tests can prove anything with.</para>
/// <para>The pair mirrors the deployment (<c>docker/sqlserver/initdb/10-principals.sql</c>) rather
/// than inventing a shape of its own. <see cref="MigratorRole"/> owns the schemas, everything
/// migrations create and the probe table; <see cref="AppRole"/> owns nothing and is what the host,
/// the validator and every isolation test connect as, reaching the migrator's tables through
/// <c>db_datareader</c>/<c>db_datawriter</c>. That separation is what
/// <see cref="TheAppRoleCannotDisableItsOwnIsolation"/> and
/// <see cref="TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator"/> exist to prove: one
/// statement, refused for the principal serving requests and accepted for the one that is not.</para>
/// <para><b>What moving to SQL Server changed about this suite, in one place.</b> The engine has no
/// owner exemption from row-level security and no <c>BYPASSRLS</c>, so <c>FORCE</c> has no
/// counterpart and needs none — a policy filters <c>sa</c> as readily as anyone else, which
/// <see cref="TheOwnerDoesNotBypassThePolicy"/> now demonstrates rather than works around. What
/// remains reachable is <i>disabling</i> the policy, so every test that used to turn on the
/// read-time exemption now turns on <c>ALTER SECURITY POLICY … WITH (STATE = OFF)</c> instead. The
/// verdicts are unchanged; the statement that produces them is not.</para>
/// <para><b><c>Max Pool Size=1</c>.</b> Rebuilt here rather than in <c>SqlServerFixture</c>, whose
/// five-connection cap is load-bearing for the rest of the suite. One connection is what makes
/// <see cref="TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection"/> deterministic: the two scopes it
/// opens are guaranteed the same physical connection, so a leaked setting would be observed rather
/// than gambled on. Read that test's own comment before trusting it as a control — SqlClient's
/// <c>sp_reset_connection</c>, not the interceptor, is what it currently pins.</para>
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
    /// <c>gymos_app</c>. In no role that carries ALTER ANY SECURITY POLICY, because a principal that can
    /// disable the policy is not constrained by it; and the owner of nothing, because an owner can change
    /// a table out from under the policy that protects it.
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
    /// Kept so <see cref="CreatePrivilegedContext"/> can still reach the database through it after
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

    public TenantIsolationTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

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
        string isolatedDatabaseName = new SqlConnectionStringBuilder(appConnectionString).InitialCatalog;

        _bootstrapConnectionString = new SqlConnectionStringBuilder(_sqlServer.BootstrapConnectionString)
        {
            InitialCatalog = isolatedDatabaseName,

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
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
        await _sqlServer.DropDatabaseAsync(_harnessRoleConnectionString);
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
        // The BLOCK predicate is what refuses it, and unlike its PostgreSQL predecessor this test now
        // pins exactly that. There, deleting `WITH CHECK` did NOT make the test fail — PostgreSQL falls
        // back to the `USING` expression as the check for INSERT and UPDATE when `WITH CHECK` is
        // absent, and the two expressions were identical — so it pinned only that the write path was
        // policed at all, not which clause policed it.
        //
        // SQL Server has no such fallback: a policy with only a FILTER predicate reads safely and
        // writes across tenants without complaint. Removing `ADD BLOCK PREDICATE` from
        // TenantProbeSchema turns this red, which is the assertion the original could not make.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => SeedAsAsync(actingAs: TenantA, stampedWith: TenantB));
    }

    [Fact]
    public async Task TheOwnerDoesNotBypassThePolicy()
    {
        // Under PostgreSQL this was the test that justified FORCE: an owner is exempt from its own
        // table's policies unless FORCE is set, so without it the owner's reads filtered nothing.
        //
        // SQL Server has no owner exemption to switch on or off. A security policy applies to every
        // principal that queries the table — the schema owner, dbo and sysadmin included — so what
        // FORCE had to be asked for here is simply the engine's behaviour.
        //
        // The test stays anyway, and is worth more than a restatement of the manual. It is the only
        // thing in the suite that would notice if that ever stopped being true: a future SQL Server
        // adding an owner exemption, a policy left disabled at runtime by something with the rights to
        // do it, or a migration that recreates the policy over the wrong table. The property is what
        // is asserted; which mechanism provides it is not this test's business.
        //
        // The owner is the migrator, not the principal serving requests — asserted first, because
        // observing this through a non-owner would prove nothing either way.
        Assert.True(await TheMigratorOwnsTheProbeTableAsync());

        await SeedAsync(TenantA, "a-note");
        await SeedAsync(TenantB, "b-note");

        Assert.Single(await NotesAsAsync(TenantA));

        // The owner, with no tenant set. Confirmed to detect the condition rather than merely restate
        // it: running `ALTER SECURITY POLICY ... WITH (STATE = OFF)` as the migrator immediately above
        // this line turns it into `Expected: 0, Actual: 2`.
        Assert.Equal(0, await ProbeRowsVisibleToTheOwnerAsync());
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
    public async Task APrincipalThatCanDisableRowLevelSecurityFailsTheBoot()
    {
        // A correctly protected table plus a privileged connection is still a platform one statement
        // away from serving every tenant's rows to every caller. The per-table state the validator
        // checks is necessary and not sufficient.
        //
        // What makes the connection dangerous changed with the engine. PostgreSQL exempted a superuser
        // from policies at READ time, so the danger was silent and immediate. SQL Server has no such
        // exemption — `sa` is filtered like anyone else, which
        // TheOwnerDoesNotBypassThePolicy demonstrates — so the danger is that `sa` holds
        // ALTER ANY SECURITY POLICY and can switch the policy off whenever it likes. Different
        // mechanism, same verdict: this connection must not serve requests.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorAsPrivilegedPrincipalAsync().ValidateAsync(CancellationToken.None));

        // "row-level security" would NOT distinguish this from a table failure: InspectAsync's own
        // messages contain that same phrase, so the assertion would pass on a validator that never
        // looked at the principal at all. ALTER SECURITY POLICY appears only in the principal message.
        Assert.Contains("ALTER SECURITY POLICY", error.Message, StringComparison.Ordinal);
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
        await using SqlServerFixture.DatabaseLease unmigratedLease = await _sqlServer.LeaseUnmigratedDatabaseAsync("tenant_isolation_pending");
        string unmigrated = unmigratedLease.ConnectionString;

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
    public async Task APrivilegedPrincipalFailsTheBootEvenWithNothingTenantScoped()
    {
        // The check used to be conditional on something implementing ITenantScoped, purely because the
        // deployment connected as a superuser and an unconditional check would have failed every boot.
        // That is fixed, so the condition goes — which also closes the hole where an entity that forgets
        // the marker silently switches the whole control off.
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorWithNoTenantScopedEntitiesAsPrivilegedPrincipalAsync()
                .ValidateAsync(CancellationToken.None));

        Assert.Contains("ALTER SECURITY POLICY", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenantScopedTableOwnedByTheConnectedRoleFailsTheBoot()
    {
        // The migrator/app split is the reason an injected statement cannot subvert isolation. If the
        // connecting principal can ALTER the table, it can change the table out from under the policy
        // that protects it and the split has silently stopped existing — so the boot asserts the
        // separation rather than trusting the deployment.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ValidatorOwningTheProbeAsync().ValidateAsync(CancellationToken.None));

        // "ALTER", not "owns". SQL Server tables have no owner of their own — they inherit the
        // schema's — so the validator asks the question that actually matters, whether the principal
        // can alter the table, rather than the one PostgreSQL could answer directly.
        Assert.Contains("ALTER this table", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantingTheAppRoleMembershipInTheMigratorFailsTheBoot()
    {
        // The ownership check must test effective privilege, not name equality. Adding the app
        // principal to db_ddladmin is a plausible way for a future increment to "fix" a permission
        // error, and nothing in 10-principals.sql forbids it — but it hands the app principal ALTER on
        // every table in the database, so changing the probe out from under its policy comes back
        // within reach while USER_NAME() still reads as the app principal. A check written as
        // `USER_NAME(schema.principal_id) = USER_NAME()` reports clean through exactly that, which is
        // the green-while-proving-nothing shape this increment exists to remove.
        //
        // db_ddladmin is the SQL Server analogue of `GRANT migrator TO app`. It is not the only route —
        // a direct `GRANT ALTER ON platform.tenant_probe` would do as well — but it is the one a tired
        // engineer reaches for, and it is the one that leaves the principal's own name unchanged.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);
        await ExecuteAsync(_bootstrapConnectionString, $"ALTER ROLE db_ddladmin ADD MEMBER [{AppRole}];");

        try
        {
            // First, that the risk is real rather than theoretical: a statement the app principal
            // could not previously issue against the probe now succeeds, because role membership is
            // effective immediately. Reverted on the next line, so the assertion below is about the
            // privilege and not about the column this one just added.
            await ExecuteAsAppRoleAsync("ALTER TABLE platform.tenant_probe ADD forged_column int NULL;");
            await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe DROP COLUMN forged_column;");

            // And that the validator sees it. USER_NAME() is still the app principal throughout, so
            // only a check written in terms of effective privilege can catch this.
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ValidatorAsync().ValidateAsync(CancellationToken.None));

            Assert.Contains("ALTER", error.Message, StringComparison.OrdinalIgnoreCase);
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
            await ExecuteAsync(_bootstrapConnectionString, $"ALTER ROLE db_ddladmin DROP MEMBER [{AppRole}];");
        }
    }

    [Fact]
    public async Task TheAppRoleCannotDisableItsOwnIsolation()
    {
        // The reason for two principals rather than one unprivileged principal. Disabling a security
        // policy is a single statement, and that statement is within reach of the SQL injection
        // row-level security mostly exists to contain. The app principal holds neither
        // ALTER ANY SECURITY POLICY nor membership in a role that carries it, so SQL Server refuses it.
        //
        // The statement changed with the engine. Under PostgreSQL the reachable attack was
        // `ALTER TABLE ... NO FORCE ROW LEVEL SECURITY`, which turns the owner exemption back on;
        // SQL Server has no owner exemption and therefore no NO FORCE, but it does have
        // `ALTER SECURITY POLICY ... WITH (STATE = OFF)`, which switches the policy off outright. That
        // is strictly the bigger hammer of the two, and it is the one this proves is out of reach.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        SqlException error = await Assert.ThrowsAsync<SqlException>(
            () => ExecuteAsAppRoleAsync(
                "ALTER SECURITY POLICY platform.tenant_probe_isolation WITH (STATE = OFF);"));

        Assert.Equal(CannotFindTheObjectOrNoPermission, error.Number);
    }

    /// <summary>
    /// 33268 — <c>Cannot find the object "…" because it does not exist or you do not have permissions</c>.
    /// </summary>
    /// <remarks>
    /// SQL Server deliberately conflates "absent" and "forbidden" here, so that a principal cannot map
    /// the schema by watching which names produce which error. That ambiguity means this number alone
    /// does not prove a privilege refusal — a typo in the policy name would produce it too. What
    /// removes the ambiguity is the pairing:
    /// <see cref="TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator"/> disables and
    /// re-enables this exact policy by this exact name, so the object demonstrably exists and is
    /// demonstrably alterable — by the other principal. Neither test is worth much alone; together
    /// they are the whole two-principal argument.
    /// </remarks>
    private const int CannotFindTheObjectOrNoPermission = 33268;

    [Fact]
    public async Task TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator()
    {
        // The control for the test above: it must fail for a privilege reason, not because the statement
        // is malformed, the policy is missing, or the connection is broken. See the remarks on
        // CannotFindTheObjectOrNoPermission for why that control is load-bearing here in a way it was
        // not under PostgreSQL, whose 42501 said "insufficient privilege" outright.
        //
        // The state is read back rather than letting "no exception" stand as the pass condition. Not
        // throwing is an implicit assertion, and an implicit assertion is one a later `try`/`catch`
        // around ExecuteAsMigratorRoleAsync would neuter in silence — leaving a control that passes
        // unconditionally and therefore certifies nothing about the test it exists to support.
        await CreateProbeAsync(TenantProbeSchema.CreateSql);

        await ExecuteAsMigratorRoleAsync(
            "ALTER SECURITY POLICY platform.tenant_probe_isolation WITH (STATE = OFF);");
        Assert.False(await ProbePolicyIsEnabledAsync());

        await ExecuteAsMigratorRoleAsync(
            "ALTER SECURITY POLICY platform.tenant_probe_isolation WITH (STATE = ON);");
        Assert.True(await ProbePolicyIsEnabledAsync());
    }

    [Fact]
    public async Task ATableCreatedByTheMigratorIsUsableByTheAppRoleWithNoExplicitGrant()
    {
        // The piece most likely to be subtly wrong, and its production failure mode is a permission
        // error on a table a future migration has only just added. Nothing here grants anything: the
        // INSERT works only because SqlServerFixture puts the app principal in db_datareader and
        // db_datawriter, exactly as docker/sqlserver/initdb/10-principals.sql does.
        //
        // Those two fixed roles are what replaced PostgreSQL's ALTER DEFAULT PRIVILEGES, and they are
        // a better fit for the requirement rather than merely a different spelling: default privileges
        // attach only to objects created by the role they were declared FOR, so a table created by
        // anyone else silently escaped them. Database-wide role membership has no such hole. What did
        // NOT carry over is EXECUTE — db_datareader and db_datawriter cover tables and nothing else —
        // which is why the schema-level EXECUTE grant exists separately and why
        // TheTwoFunctionsThatIssueDdlOnTheAppRoleSBehalfWorkAndStayContained is a distinct test.
        await ExecuteAsMigratorRoleAsync(
            "CREATE TABLE platform.default_privilege_probe (id uniqueidentifier NOT NULL PRIMARY KEY);");

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

        // 1. The app principal can call them at all, and they do the work.
        //
        // Both are procedures with OUTPUT parameters rather than functions called from a SELECT, and
        // that is forced rather than chosen: a SQL Server function may not issue DDL, and issuing DDL
        // on the caller's behalf is the entire reason these two exist. See the migration.
        long invoiceNumber = await ProcedureScalarAsAppRoleAsync<long>(
            "platform.next_invoice_number", "bigint", ("@year", 2026));

        Assert.Equal(1, invoiceNumber);

        // A month far enough out that neither the migration's three-month lookahead nor the host's
        // own startup maintenance has already created it — otherwise the function returns 0 for the
        // uninteresting reason that there was nothing to do, and the privilege is never exercised.
        int created = await ProcedureScalarAsAppRoleAsync<int>(
            "platform.ensure_audit_log_partitions",
            "int",
            ("@from_month", new DateTime(2099, 1, 1)),
            ("@months_ahead", 0));

        Assert.Equal(1, created);

        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        // 2. EXECUTE AS OWNER bought the app principal the right to CALL them. It must not have handed
        // it ownership of what they produce, or the two-principal split would be undone by the very fix
        // that made invoicing work. The objects belong to the schema, which belongs to the migrator.
        await using (SqlCommand owner = connection.CreateCommand())
        {
            owner.CommandText =
                """
                SELECT USER_NAME(COALESCE(sq.principal_id, s.principal_id))
                FROM sys.sequences sq
                JOIN sys.schemas s ON s.schema_id = sq.schema_id
                WHERE s.name = 'platform' AND sq.name = 'invoice_number_seq_2026'
                """;

            Assert.Equal(MigratorRole, (string)(await owner.ExecuteScalarAsync())!);
        }

        // 3 and 4. The two properties that make an elevated module safe rather than a
        // privilege-escalation vector, read from the catalog rather than from the migration's text.
        //
        // What is checked here differs from the PostgreSQL version, because one of its two hazards does
        // not exist on SQL Server and the other inverts:
        //
        //   * search_path. PostgreSQL resolves unqualified names in a SECURITY DEFINER function against
        //     the CALLER's search_path, so an unpinned path lets a caller point the elevated function at
        //     a schema it writes and have its own code run as the owner. That is why the original
        //     asserted `proconfig LIKE '%search_path=%'`. SQL Server resolves unqualified names inside a
        //     module against the MODULE's own schema; there is no caller-controlled path, so there is no
        //     equivalent flag and nothing to assert. Its place is taken by the check below that the
        //     module is elevated at all.
        //   * PUBLIC execute. `CREATE FUNCTION` grants EXECUTE to PUBLIC on PostgreSQL, so the original
        //     had to prove the default was revoked. `CREATE PROCEDURE` grants it to nobody here, so the
        //     hazard runs the other way — the risk is a grant that is too WIDE having been added, which
        //     is what the public_may_execute check still catches.
        await using SqlCommand definers = connection.CreateCommand();
        definers.CommandText =
            """
            SELECT o.name,
                   CONVERT(bit, CASE WHEN m.execute_as_principal_id = -2 THEN 1 ELSE 0 END) AS elevated,
                   USER_NAME(s.principal_id) AS schema_owner,
                   CONVERT(bit, CASE WHEN EXISTS (
                       SELECT 1 FROM sys.database_permissions dp
                       JOIN sys.database_principals gp ON gp.principal_id = dp.grantee_principal_id
                       WHERE dp.major_id = o.object_id
                         AND dp.permission_name = 'EXECUTE'
                         AND dp.state_desc = 'GRANT'
                         AND gp.name = 'public') THEN 1 ELSE 0 END) AS public_may_execute
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id = o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
            WHERE s.name = 'platform'
              AND o.name IN ('next_invoice_number', 'ensure_audit_log_partitions')
            ORDER BY o.name
            """;

        var inspected = new List<string>();

        await using (SqlDataReader reader = await definers.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                string name = reader.GetString(0);
                inspected.Add(name);

                // -2 is SQL Server's sentinel for OWNER in sys.sql_modules. NULL there would mean
                // CALLER — the default, and the bug this whole test exists to prevent: a module that
                // issues DDL as whoever called it, which for gymos_app is nobody with DDL rights.
                // A positive id would mean EXECUTE AS 'some named user', which is not what was asked
                // for and would pin the module to a principal that can be dropped.
                Assert.True(reader.GetBoolean(1), $"{name} is not EXECUTE AS OWNER.");

                // Not merely elevated — elevated to the MIGRATOR. `EXECUTE AS OWNER` resolves to the
                // schema's owner at execution time, so a schema accidentally left owned by the app
                // principal would leave these modules reporting as elevated while conferring nothing.
                //
                // Read from the schema rather than from the module: USER_NAME(-2) is NULL, because -2
                // is a sentinel and not a principal id, so asking who the module executes as answers
                // nothing. The schema's owner IS the answer.
                Assert.Equal(MigratorRole, reader.GetString(2));

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

        // The PostgreSQL version asked `rolsuper OR rolbypassrls`, because those two attributes exempt
        // a role from row-level security outright. SQL Server has no read-time exemption at all, so the
        // equivalent question is "can this principal turn the policy OFF" — sysadmin, db_owner and
        // ALTER ANY SECURITY POLICY are the three ways, and they are exactly what
        // TenantIsolationValidator refuses to boot on.
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT CONVERT(bit, CASE WHEN ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0) = 1
                                       OR ISNULL(IS_ROLEMEMBER('db_owner'), 0) = 1
                                       OR ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY SECURITY POLICY'), 0) = 1
                                     THEN 1 ELSE 0 END)
            """;

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
        // asked about the `platform` schema by name and died when it did not exist yet, which is a test
        // failing for the wrong reason rather than a red phase worth trusting.
        //
        // `schemas_owned` counts schemas rather than tables, and that is the SQL Server shape of the
        // question rather than a weaker version of it. A table here has no owner of its own unless one
        // is set explicitly — it inherits the schema's — so "owns no schema" is what "owns no table"
        // means, and it is the stronger claim: owning the schema is what would confer ownership of
        // every table a future migration adds to it.
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM sys.schemas s
                  WHERE s.principal_id = DATABASE_PRINCIPAL_ID()
                    AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA'))                     AS schemas_owned,
                CONVERT(bit, ISNULL(IS_ROLEMEMBER('db_owner'), 0))                       AS owns_database,
                CONVERT(bit, CASE WHEN ISNULL(IS_ROLEMEMBER('db_ddladmin'), 0) = 1
                                    OR ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE TABLE'), 0) = 1
                                    OR ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CREATE SCHEMA'), 0) = 1
                                  THEN 1 ELSE 0 END)                                     AS can_create
            """;

        await context.Database.OpenConnectionAsync();

        try
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            Assert.Equal(0, reader.GetInt32(0));
            Assert.False(reader.GetBoolean(1));

            // The right to create is what lets a principal add a schema or table and own everything in
            // it, so owning nothing today means little without this. Asked three ways because there are
            // three routes to it — the role, and each of the two permissions it implies.
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
    /// deliberately avoids, because <c>sa</c> holds ALTER ANY SECURITY POLICY and can therefore switch
    /// security entirely, above whatever the table flags say — with the probe in the model, so there is
    /// a tenant-scoped table for it to have an opinion about.
    /// </summary>
    private TenantIsolationValidator ValidatorAsPrivilegedPrincipalAsync() =>
        new(CreatePrivilegedContext(withTenantProbe: true), NullLogger<TenantIsolationValidator>.Instance);

    /// <summary>
    /// A validator over the same superuser connection, but the unmodified <see cref="AppDbContext"/>
    /// model — no <see cref="IModelCustomizer"/> replacement, so nothing implements
    /// <see cref="ITenantScoped"/> and the validator has no subject to check the role against.
    /// </summary>
    private TenantIsolationValidator ValidatorWithNoTenantScopedEntitiesAsPrivilegedPrincipalAsync() =>
        new(CreatePrivilegedContext(withTenantProbe: false), NullLogger<TenantIsolationValidator>.Instance);

    /// <summary>
    /// A validator whose connection is <see cref="MigratorRole"/> — the role that created the probe
    /// table and therefore owns it. Not privileged over security policies, deliberately: the only
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
    private AppDbContext CreatePrivilegedContext(bool withTenantProbe) =>
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
    /// Whether <see cref="MigratorRole"/> — and not the principal the suite serves requests as — owns
    /// the probe table. The precondition that makes <see cref="TheOwnerDoesNotBypassThePolicy"/> a test
    /// of the risk rather than of nothing.
    /// </summary>
    /// <remarks>
    /// Ownership is read from the schema, not the table. SQL Server tables have no owner of their own
    /// unless one is set explicitly (<c>sys.tables.principal_id</c> is NULL in the ordinary case, which
    /// means "owned by the schema owner"), so the question PostgreSQL answered with
    /// <c>pg_get_userbyid(relowner)</c> is answered here by the schema's <c>principal_id</c>. COALESCE
    /// covers the explicit-owner case so this keeps reporting the truth if one is ever set.
    /// </remarks>
    private async Task<bool> TheMigratorOwnsTheProbeTableAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT CONVERT(bit, CASE WHEN USER_NAME(COALESCE(t.principal_id, s.principal_id)) = USER_NAME()
                                     THEN 1 ELSE 0 END)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'platform' AND t.name = 'tenant_probe'
            """;

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Whether the probe's security policy is currently enabled, read straight from
    /// <c>sys.security_policies</c> — so a test that switches it off can assert it actually went off.
    /// </summary>
    /// <remarks>
    /// This replaces the PostgreSQL version's <c>relforcerowsecurity</c> read. The two flags are not
    /// the same thing and the substitution is not a rename: <c>FORCE</c> governed whether the owner was
    /// exempt, while <c>is_enabled</c> governs whether the policy filters anybody at all. SQL Server
    /// has no owner exemption to switch, so the enabled flag is the only thing left that can be turned
    /// off — and it is a strictly larger lever, which is why the test that proves the app principal
    /// cannot reach it matters more here than its predecessor did.
    /// </remarks>
    private async Task<bool> ProbePolicyIsEnabledAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.is_enabled
            FROM sys.security_policies p
            JOIN sys.schemas s ON s.schema_id = p.schema_id
            WHERE s.name = 'platform' AND p.name = 'tenant_probe_isolation'
            """;

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Rows the probe table yields to the principal that owns it, with no tenant set.
    /// </summary>
    /// <remarks>
    /// Zero, and the reason changed. Under PostgreSQL this was zero only because
    /// <c>FORCE ROW LEVEL SECURITY</c> was set — without it the owner saw every row. On SQL Server the
    /// owner is subject to the policy unconditionally, so zero is what the engine does by default.
    /// The assertion is kept because the property is what matters, not the mechanism that provides it:
    /// if a future change ever introduces an exemption, or the policy is disabled at runtime, this is
    /// what notices.
    /// </remarks>
    private async Task<int> ProbeRowsVisibleToTheOwnerAsync()
    {
        await using var connection = new SqlConnection(_migratorConnectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM platform.tenant_probe";

        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a single-value query on the app-principal connection and returns its result.</summary>
    private async Task<T> ScalarAsAppRoleAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Calls a stored procedure on the app-principal connection and returns its <c>@result</c> OUTPUT
    /// parameter, declared as <paramref name="resultType"/>.
    /// </summary>
    /// <remarks>
    /// The PostgreSQL original needed nothing like this: its two runtime modules were functions, so
    /// <c>ScalarAsAppRoleAsync</c> could simply SELECT from them. Both had to become procedures on SQL
    /// Server — a function here may not issue DDL, and creating a sequence or splitting a partition is
    /// what they are for — and a procedure returns through an OUTPUT parameter rather than a result set.
    /// </remarks>
    private async Task<T> ProcedureScalarAsAppRoleAsync<T>(
        string procedure,
        string resultType,
        params (string Name, object Value)[] arguments)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        await using SqlCommand command = connection.CreateCommand();
        command.CommandType = System.Data.CommandType.StoredProcedure;
        command.CommandText = procedure;

        foreach ((string name, object value) in arguments)
        {
            command.Parameters.AddWithValue(name, value);
        }

        // Named for what each procedure actually calls it, which differs — @number and @created.
        // Positional binding is what makes one helper serve both: SqlClient matches an unnamed
        // trailing parameter by position when the name is absent from the procedure's signature, so
        // the output parameter is declared last and left unnamed here.
        SqlParameter result = command.Parameters.Add(
            new SqlParameter
            {
                ParameterName = resultType == "bigint" ? "@number" : "@created",
                SqlDbType = resultType == "bigint" ? System.Data.SqlDbType.BigInt : System.Data.SqlDbType.Int,
                Direction = System.Data.ParameterDirection.Output,
            });

        await command.ExecuteNonQueryAsync();

        return (T)result.Value!;
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
