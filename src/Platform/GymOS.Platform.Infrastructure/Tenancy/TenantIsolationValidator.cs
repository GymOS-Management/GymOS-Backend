using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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
/// <para><b>Public, not internal.</b> It is invoked from <c>Program.cs</c> in the
/// <c>GymOS.WebApi</c> host, which has no <c>InternalsVisibleTo</c> grant onto this assembly — the
/// same reason <c>FeatureExceptionMaintainer</c> is public rather than internal.</para>
/// </remarks>
public sealed class TenantIsolationValidator
{
    private readonly AppDbContext _dbContext;

    public TenantIsolationValidator(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            return; // first boot: the tables do not exist yet, and the migration will create them protected
        }

        var problems = new List<string>();

        foreach (var (schema, table) in TenantScopedTables())
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
