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
/// <para><b>The connected role is checked only when there is something to protect.</b> A role with
/// <c>rolsuper</c> or <c>rolbypassrls</c> bypasses row-level security entirely, above whatever the
/// table flags say — <c>FORCE</c> does not constrain it. But nothing in production implements
/// <see cref="ITenantScoped"/> yet, and this application's own deployment connects as a superuser
/// (<c>docker-compose.yml</c>'s <c>POSTGRES_USER</c>), so an unconditional check would fail every
/// developer's boot today for a risk that does not exist until the first tenant-scoped table lands.
/// The day it does, this fires and names the role, instead of quietly serving every tenant's rows to
/// every caller.</para>
/// <para><b>Both of its skip paths say so out loud.</b> Either early return switches this control off
/// entirely — including the connected-role check — and a security control that turns itself off must
/// not do it silently. A deployment with <c>MigrateOnStartup=false</c> and a migration left pending
/// would otherwise disable R14 with nothing in the log to find afterwards. The warnings name which
/// condition did it, so "was this control running?" is answerable from the boot log.</para>
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
        Message = "Tenant isolation validation (R14) checked nothing: no entity in the EF model " +
            "implements ITenantScoped, so neither the per-table checks nor the connected-role " +
            "BYPASSRLS check ran. Expected while no production table is tenant-scoped; once one is, " +
            "an entity missing the ITenantScoped marker produces this same message.")]
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

        var tenantScopedTables = TenantScopedTables().ToList();

        if (tenantScopedTables.Count == 0)
        {
            // Nothing to protect yet, so nothing to check — see the role note in the remarks. Logged
            // because this is also what a forgotten ITenantScoped marker looks like from in here.
            LogNoTenantScopedEntities(_logger);
            return;
        }

        var problems = new List<string>(await InspectConnectedRoleAsync(cancellationToken));

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
        // matters most: this application connects as the owner of its tables, and PostgreSQL exempts
        // an owner from its own policies, so checking `relrowsecurity` alone passes on a table whose
        // policies never filter a single row (D5).
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
                   AND col.column_name = 'tenant_id') AS has_tenant_id
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

            if (reader.GetInt64(3) == 0)
            {
                problems.Add($"  {schema}.{table}: no tenant_id column.");
            }

            if (!reader.GetBoolean(0))
            {
                problems.Add($"  {schema}.{table}: row-level security is not enabled.");
            }

            if (!reader.GetBoolean(1))
            {
                problems.Add(
                    $"  {schema}.{table}: row-level security is not FORCED, so the table owner — " +
                    "which is how this application connects — bypasses every policy on it.");
            }

            if (reader.GetInt64(2) == 0)
            {
                problems.Add($"  {schema}.{table}: no row-level security policy.");
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return problems;
    }
}
