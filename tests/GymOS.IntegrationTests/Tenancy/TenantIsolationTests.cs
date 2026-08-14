using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace GymOS.IntegrationTests.Tenancy;

/// <summary>
/// R14's mechanism, proven rather than installed: a tenant sees only its own rows, a write cannot be
/// forged for another tenant, the table owner does not bypass the policy, platform scope reads
/// nothing rather than everything, an unprotected table fails the boot, and the session setting does
/// not survive into the next use of a pooled connection.
/// </summary>
/// <remarks>
/// <para><b>The suite's own database, and its own database role.</b> Every assertion here counts rows,
/// so the suite takes an isolated database. It also takes a dedicated <c>NOSUPERUSER NOBYPASSRLS</c>
/// role and hands that role ownership of the database — because the container's bootstrap role is a
/// superuser, and PostgreSQL exempts superusers from row-level security unconditionally, <c>FORCE</c>
/// included. Run as the bootstrap role, all six tests below would report "no isolation" no matter what
/// the policies said. The dedicated role reproduces the production shape D5 describes: the application
/// connects as <c>gymos</c>, owns its tables, and is subject to their policies only because they are
/// forced.</para>
/// <para><b><c>Maximum Pool Size=1</c>.</b> Rebuilt here rather than in <c>PostgresFixture</c>, whose
/// five-connection cap is load-bearing for the rest of the suite. One connection is what makes
/// <see cref="TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection"/> deterministic: the two scopes it
/// opens are guaranteed the same physical connection, so a leaked setting would be observed rather
/// than gambled on.</para>
/// </remarks>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class TenantIsolationTests : IAsyncLifetime
{
    private static readonly Guid TenantA = new("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid TenantB = new("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    /// <summary>
    /// The role the suite connects as. Not a superuser and not <c>BYPASSRLS</c>: those attributes
    /// defeat row-level security outright, so a suite running under them proves nothing.
    /// </summary>
    private const string ApplicationRole = "gymos_tenant_isolation";

    /// <summary>A password for a throwaway role inside a throwaway container; not a secret.</summary>
    private const string ApplicationRolePassword = "tenant-isolation-role";

    /// <summary>
    /// Creates the role once per container and hands it this database. Ownership of <c>public</c>
    /// moves too, or the role cannot create <c>__EFMigrationsHistory</c> and migrations fail on the
    /// first statement.
    /// </summary>
    private static readonly string GrantDatabaseToApplicationRoleSql =
        $"""
        DO $$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{ApplicationRole}') THEN
                CREATE ROLE "{ApplicationRole}"
                    LOGIN PASSWORD '{ApplicationRolePassword}' NOSUPERUSER NOBYPASSRLS;
            END IF;
        END
        $$;

        DO $$
        BEGIN
            EXECUTE format('ALTER DATABASE %I OWNER TO %I', current_database(), '{ApplicationRole}');
        END
        $$;

        ALTER SCHEMA public OWNER TO "{ApplicationRole}";
        """;

    private readonly PostgresFixture _postgres;
    private readonly List<IServiceScope> _scopes = [];
    private string _connectionString = null!;
    private AuthenticationHost _host = null!;

    public TenantIsolationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string bootstrapConnectionString = await _postgres.CreateIsolatedDatabaseAsync("tenant_isolation");

        await ExecuteAsync(bootstrapConnectionString, GrantDatabaseToApplicationRoleSql);

        _connectionString = new NpgsqlConnectionStringBuilder(bootstrapConnectionString)
        {
            Username = ApplicationRole,
            Password = ApplicationRolePassword,

            // One physical connection, so connection reuse is certain rather than lucky (D6).
            MaxPoolSize = 1,
        }.ConnectionString;

        // Migrations before the host boots, for three reasons. They create the `platform` schema the
        // probe table lives in; TenantIsolationValidator returns early and silently while any
        // migration is pending, so a database that has not been migrated would make
        // AProtectedTablePassesValidation_AnUnprotectedOneFailsTheBoot assert on a validator that
        // never looked at anything; and EF compares the model against the migrations snapshot when it
        // migrates, so the host — whose model carries the probe — must not be the one to do it.
        await using (AppDbContext migrator = CreateMigrationContext())
        {
            await migrator.Database.MigrateAsync();
        }

        // Before the host, not after: Program.cs runs the real validator during startup, and the probe
        // is a mapped ITenantScoped entity from this point on. An absent (or unprotected) probe table
        // is exactly the condition the validator kills the boot for.
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
        // The test that justifies FORCE. This application connects as the owner of its tables, and
        // PostgreSQL exempts an owner from its own policies — so WITHOUT `FORCE ROW LEVEL SECURITY`
        // this passes while filtering nothing, and every other test here passes with it.
        //
        // Confirm the connected role really is the owner, or the test proves nothing about the risk.
        Assert.True(await ConnectedRoleOwnsProbeTableAsync());

        await SeedAsync(TenantA, "a-note");
        await SeedAsync(TenantB, "b-note");

        Assert.Single(await NotesAsAsync(TenantA));
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
    public async Task TheTenantDoesNotLeakIntoTheNextUseOfAPooledConnection()
    {
        // Maximum Pool Size=1 makes this deterministic: both scopes below are guaranteed the same
        // physical connection, so if the setting survived the first scope the second would read A's
        // rows. It writes on every open precisely so it cannot (D6).
        await SeedAsync(TenantA, "a-note");

        IReadOnlyList<string> asA = await NotesAsAsync(TenantA);
        IReadOnlyList<string> asPlatform = await NotesWithNoTenantAsync();

        Assert.Single(asA);
        Assert.Empty(asPlatform);
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
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
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

    private Task CreateProbeAsync(string createSql) => ExecuteAsync(_connectionString, createSql);

    private Task DropProbeAsync() => ExecuteAsync(_connectionString, TenantProbeSchema.DropSql);

    /// <summary>
    /// Whether the role this suite connects as owns the probe table — the precondition that makes
    /// <see cref="TheOwnerDoesNotBypassThePolicy"/> a test of the risk rather than of nothing.
    /// </summary>
    private async Task<bool> ConnectedRoleOwnsProbeTableAsync()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT pg_get_userbyid(c.relowner) = current_user
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND c.relname = 'tenant_probe'
            """;

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
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
