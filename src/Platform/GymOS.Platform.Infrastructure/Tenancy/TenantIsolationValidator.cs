using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// R14's startup persistence validation: every tenant-scoped table carries <c>tenant_id</c> and is
/// protected by a forced row-level security policy.
/// </summary>
/// <remarks>
/// <para><b>Fails the boot.</b> A platform that will not start is a visible outage; a platform serving
/// cross-tenant reads is an invisible breach. <c>Program.cs</c> logs a startup exception as Fatal and
/// rethrows, so the host dies and the container restarts.</para>
/// <para><b>It does not swallow.</b> <c>AuditLogPartitionMaintainer</c> and
/// <c>IdempotencyRecordMaintainer</c> catch <c>NpgsqlException</c> so a schemaless database can boot;
/// this must not copy them. It skips only while migrations are genuinely pending — a real first-boot
/// state, and the guard <c>SuperAdminSeeder</c> already has. Anything else is fatal, because a
/// security control that swallows "the table isn't there" passes on an empty database and on every
/// misconfiguration that resembles one.</para>
/// <para><b>The connected role is checked unconditionally.</b> A role with <c>rolsuper</c> or
/// <c>rolbypassrls</c> bypasses row-level security entirely, above whatever the table flags say —
/// <c>FORCE</c> does not constrain it. This check used to be gated on something implementing
/// <see cref="ITenantScoped"/>, purely because the deployment connected as a superuser
/// (<c>docker-compose.yml</c>'s <c>POSTGRES_USER</c>) and an unconditional check would have failed
/// every boot. The deployment now connects as <c>gymos_app</c>, so the gate is gone — and the gate
/// was itself the hole: an entity that forgot the marker produced zero tenant-scoped tables and
/// switched the whole control, role check included, silently off. The role a host connects as is a
/// property of the deployment, not of the model, so nothing about the model may excuse checking it.</para>
/// <para><b>And the connected role must not own what it is isolated from.</b> An owner can execute
/// <c>ALTER TABLE ... NO FORCE ROW LEVEL SECURITY</c> and switch a table's own isolation off in one
/// statement — well within reach of the SQL injection row-level security mostly exists to contain.
/// Two roles is what removes that: <c>gymos_migrator</c> owns the schema and runs DDL,
/// <c>gymos_app</c> owns nothing and serves requests. This asserts that separation rather than
/// trusting the deployment to have kept it — and asks whether the connected role can <i>act as</i> the
/// owner, not whether it is <i>named</i> the owner, because a granted membership in the owning role
/// confers the same power while leaving <c>current_user</c> unchanged.</para>
/// <para><b>Its remaining skip path says so out loud.</b> The pending-migrations return switches this
/// control off entirely, and a security control that turns itself off must not do it silently. A
/// deployment with <c>MigrateOnStartup=false</c> and a migration left pending would otherwise disable
/// R14 with nothing in the log to find afterwards. Having nothing tenant-scoped is no longer a skip —
/// it logs and falls through, so the role checks still run — but it is warned about for the same
/// reason: it is also what a forgotten <see cref="ITenantScoped"/> marker looks like from in here.</para>
/// <para><b>Public, not internal.</b> It is invoked from <c>Program.cs</c> in the
/// <c>GymOS.WebApi</c> host, which has no <c>InternalsVisibleTo</c> grant onto this assembly — the
/// same reason <c>FeatureExceptionMaintainer</c> is public rather than internal.</para>
/// </remarks>
public sealed partial class TenantIsolationValidator
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<TenantIsolationValidator> _logger;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Tenant isolation validation (R14) was SKIPPED: the database has pending migrations, " +
            "so the tables it would check may not exist yet. No tenant-scoped table is verified on " +
            "this boot. Expected only on a first boot; if migrations are applied out of band " +
            "(Database:MigrateOnStartup=false), this control is off until they are.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Tenant isolation validation (R14) checked no table: no entity in the EF model " +
            "implements ITenantScoped, so the per-table checks had nothing to run against. The " +
            "connected-role checks still ran. Expected while no production table is tenant-scoped; " +
            "once one is, an entity missing the ITenantScoped marker produces this same message.")]
    private static partial void LogNoTenantScopedEntities(ILogger logger);

    /// <summary>Creates the validator with its database context and logger.</summary>
    public TenantIsolationValidator(AppDbContext dbContext, ILogger<TenantIsolationValidator> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            // First boot: the tables do not exist yet, and the migration will create them protected.
            // Logged rather than returned silently — see the remarks.
            LogSkippedPendingMigrations(_logger);
            return;
        }

        // Before the zero-entities branch, and collected regardless of it: the role a host connects as
        // is a property of the deployment, not of the EF model, so an empty model must not be able to
        // excuse checking it.
        var problems = new List<string>(await InspectConnectedRoleAsync(cancellationToken));

        var tenantScopedTables = TenantScopedTables().ToList();

        if (tenantScopedTables.Count == 0)
        {
            // Nothing tenant-scoped to inspect per table — but this is also what a forgotten
            // ITenantScoped marker looks like from in here, so it is logged rather than passed over.
            // It falls through instead of returning: the role checks above still stand on their own.
            LogNoTenantScopedEntities(_logger);
        }

        foreach (var (schema, table) in tenantScopedTables)
        {
            problems.AddRange(await InspectAsync(schema, table, cancellationToken));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "R14 (tenant isolation) is not satisfied, so the host will not start:" +
                Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }

    /// <summary>Every mapped entity implementing <see cref="ITenantScoped"/>, with its table.</summary>
    private IEnumerable<(string Schema, string Table)> TenantScopedTables() =>
        _dbContext.Model.GetEntityTypes()
            .Where(entity => typeof(ITenantScoped).IsAssignableFrom(entity.ClrType))
            .Select(entity => (
                Schema: entity.GetSchema() ?? "public",
                Table: entity.GetTableName() ?? entity.ClrType.Name))
            .Distinct();

    /// <summary>
    /// Whether the connected role bypasses row-level security outright. PostgreSQL exempts a
    /// superuser or a <c>BYPASSRLS</c> role from every policy unconditionally — <c>FORCE</c> included
    /// — so this is checked before any table flag, because no table flag can compensate for it.
    /// </summary>
    private async Task<IReadOnlyList<string>> InspectConnectedRoleAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user";

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // A missing row is a problem, not a pass. `current_user` should always match a `pg_roles`
            // row, so there is no benign reading of its absence — and the alternative is a security
            // check with a branch that fails open, which is the one shape it must never have.
            // `InspectAsync` treats a missing row the same way, for the same reason.
            if (!await reader.ReadAsync(cancellationToken))
            {
                problems.Add(
                    "  the connected role could not be found in pg_roles, so whether it bypasses " +
                    "row-level security could not be established.");

                return problems;
            }

            if (reader.GetBoolean(1) || reader.GetBoolean(2))
            {
                problems.Add(
                    $"  the connected role \"{reader.GetString(0)}\" has BYPASSRLS (or is a " +
                    "superuser), so row-level security does not apply to it and every policy below " +
                    "is void.");
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return problems;
    }

    private async Task<IReadOnlyList<string>> InspectAsync(
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        // One round trip per table, straight from pg_catalog. `relforcerowsecurity` is the one that
        // matters most: PostgreSQL exempts a table's owner from its own policies, so checking
        // `relrowsecurity` alone passes on a table whose policies never filter a single row for the
        // owner (D5) — and `gymos_migrator` does own every table here.
        //
        // `owned_by_connected_role` goes LAST on purpose: every GetBoolean/GetInt64 below indexes this
        // SELECT positionally, so inserting anywhere else would silently re-point four existing checks.
        //
        // It asks `pg_has_role`, not `pg_get_userbyid(c.relowner) = current_user`. The name comparison
        // reads correctly and is wrong: `GRANT gymos_migrator TO gymos_app` — a plausible way to make a
        // permission error go away, and nothing forbids it — hands the app role the owner's rights
        // through inherited membership while `current_user` still reads `gymos_app`. The connected role
        // could then execute ALTER TABLE ... NO FORCE and this check would report clean. `pg_has_role`
        // with USAGE is true both when the role IS the owner and when it has been granted membership in
        // it, which is the question actually worth asking: not "is it named the owner" but "can it act
        // as the owner". Confirmed by test, not by reading the docs — see
        // GrantingTheAppRoleMembershipInTheMigratorFailsTheBoot.
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT
                c.relrowsecurity,
                c.relforcerowsecurity,
                (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid) AS policies,
                (SELECT count(*) FROM information_schema.columns col
                 WHERE col.table_schema = n.nspname
                   AND col.table_name = c.relname
                   AND col.column_name = 'tenant_id') AS has_tenant_id,
                pg_has_role(current_user, c.relowner, 'USAGE') AS owned_by_connected_role
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table
            """;

        var schemaParameter = command.CreateParameter();
        schemaParameter.ParameterName = "schema";
        schemaParameter.Value = schema;
        command.Parameters.Add(schemaParameter);

        var tableParameter = command.CreateParameter();
        tableParameter.ParameterName = "table";
        tableParameter.Value = table;
        command.Parameters.Add(tableParameter);

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                problems.Add($"  {schema}.{table}: the table does not exist.");
                return problems;
            }

            // 3: has_tenant_id
            if (reader.GetInt64(3) == 0)
            {
                problems.Add($"  {schema}.{table}: no tenant_id column.");
            }

            // 0: relrowsecurity
            if (!reader.GetBoolean(0))
            {
                problems.Add($"  {schema}.{table}: row-level security is not enabled.");
            }

            // 1: relforcerowsecurity
            if (!reader.GetBoolean(1))
            {
                problems.Add(
                    $"  {schema}.{table}: row-level security is not FORCED, so the table owner — " +
                    "the role migrations run as — bypasses every policy on it.");
            }

            // 2: policies
            if (reader.GetInt64(2) == 0)
            {
                problems.Add($"  {schema}.{table}: no row-level security policy.");
            }

            // 4: owned_by_connected_role
            if (reader.GetBoolean(4))
            {
                problems.Add(
                    $"  {schema}.{table}: the connected role owns this table — directly, or through " +
                    "membership in the role that does — so it can execute ALTER TABLE ... NO FORCE " +
                    "ROW LEVEL SECURITY and switch the table's own isolation off. Migrations must " +
                    "run as a role that is not the one serving requests, and the role serving " +
                    "requests must not be a member of it.");
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return problems;
    }
}
