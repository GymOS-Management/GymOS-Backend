using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Tenancy;

/// <summary>
/// R14's startup persistence validation: every tenant-scoped table carries <c>tenant_id</c> and is
/// protected by an enabled row-level security policy that the connected principal cannot switch off.
/// </summary>
/// <remarks>
/// <para><b>Fails the boot.</b> A platform that will not start is a visible outage; a platform serving
/// cross-tenant reads is an invisible breach. <c>Program.cs</c> logs a startup exception as Fatal and
/// rethrows, so the host dies and the container restarts.</para>
/// <para><b>It does not swallow.</b> <c>AuditLogPartitionMaintainer</c> and
/// <c>IdempotencyRecordMaintainer</c> catch <c>SqlException</c> so a schemaless database can boot;
/// this must not copy them. It skips only while migrations are genuinely pending — a real first-boot
/// state, and the guard <c>SuperAdminSeeder</c> already has. Anything else is fatal, because a
/// security control that swallows "the table isn't there" passes on an empty database and on every
/// misconfiguration that resembles one.</para>
///
/// <para><b>What changed moving from PostgreSQL, and what did not.</b> The control is the same, and
/// one of its three parts turned out to be unnecessary rather than merely different:</para>
/// <list type="bullet">
/// <item><b><c>FORCE ROW LEVEL SECURITY</c> has no counterpart, and needs none.</b> PostgreSQL exempts
/// a table's owner from its own policies unless <c>FORCE</c> is set, which is why the original checked
/// <c>relforcerowsecurity</c> rather than <c>relrowsecurity</c> alone (D5). SQL Server has no owner
/// exemption: a security policy filters every principal that queries the table, <c>dbo</c> and
/// <c>sysadmin</c> included. The forced behaviour is the only behaviour, so there is no flag whose
/// absence could quietly void a policy.</item>
/// <item><b><c>BYPASSRLS</c> has no counterpart either</b>, for the same reason — SQL Server has no
/// attribute that exempts a principal from a predicate at query time.</item>
/// <item><b>The disable path is what remains, and it is what is checked.</b> A principal cannot read
/// around a policy, but one holding <c>ALTER ANY SECURITY POLICY</c> (or <c>CONTROL</c> on the
/// database, or membership in <c>db_owner</c>) can execute <c>ALTER SECURITY POLICY … WITH (STATE =
/// OFF)</c> and switch the whole thing off in one statement — precisely the reach the PostgreSQL
/// version worried about with <c>ALTER TABLE … NO FORCE</c>, arriving through a different door. And a
/// principal holding <c>ALTER</c> on the table (through <c>db_ddladmin</c>, schema ownership, or a
/// direct grant) can change the table out from under the policy. Two principals is what removes both:
/// <c>gymos_migrator</c> owns the schemas and runs DDL, <c>gymos_app</c> owns nothing and serves
/// requests.</item>
/// </list>
///
/// <para><b>The connected principal is checked unconditionally</b>, before the per-table loop and
/// regardless of it. The principal a host connects as is a property of the deployment, not of the EF
/// model, so nothing about the model may excuse checking it — an entity that forgot the
/// <see cref="ITenantScoped"/> marker produces zero tenant-scoped tables, and gating the principal
/// check on that list being non-empty would switch the whole control silently off.</para>
///
/// <para><b>Its remaining skip path says so out loud.</b> The pending-migrations return switches this
/// control off entirely, and a security control that turns itself off must not do it silently. A
/// deployment with <c>MigrateOnStartup=false</c> and a migration left pending would otherwise disable
/// R14 with nothing in the log to find afterwards. Having nothing tenant-scoped is no longer a skip —
/// it logs and falls through, so the principal checks still run — but it is warned about for the same
/// reason: it is also what a forgotten <see cref="ITenantScoped"/> marker looks like from in here.</para>
///
/// <para><b>Public, not internal.</b> It is invoked from <c>Program.cs</c> in the
/// <c>GymOS.WebApi</c> host, which has no <c>InternalsVisibleTo</c> grant onto this assembly — the
/// same reason <c>FeatureExceptionMaintainer</c> is public rather than internal.</para>
/// </remarks>
public sealed partial class TenantIsolationValidator
{
    /// <summary>SQL Server's default schema, used when an entity declares none.</summary>
    private const string DefaultSchema = "dbo";

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
            "connected-principal checks still ran. Expected while no production table is tenant-scoped; " +
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

        // Before the zero-entities branch, and collected regardless of it: the principal a host
        // connects as is a property of the deployment, not of the EF model, so an empty model must
        // not be able to excuse checking it.
        var problems = new List<string>(await InspectConnectedPrincipalAsync(cancellationToken));

        var tenantScopedTables = TenantScopedTables().ToList();

        if (tenantScopedTables.Count == 0)
        {
            // Nothing tenant-scoped to inspect per table — but this is also what a forgotten
            // ITenantScoped marker looks like from in here, so it is logged rather than passed over.
            // It falls through instead of returning: the principal checks above stand on their own.
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
                Schema: entity.GetSchema() ?? DefaultSchema,
                Table: entity.GetTableName() ?? entity.ClrType.Name))
            .Distinct();

    /// <summary>
    /// Every way the connected principal could disable row-level security rather than merely be
    /// filtered by it, with the sentence each one produces. Ordinals index the SELECT in
    /// <see cref="InspectConnectedPrincipalAsync"/>; changing one without the other silently
    /// re-points a check.
    /// </summary>
    private static readonly (int Ordinal, string Capability)[] PrincipalDisablers =
    [
        (1, "is a member of the sysadmin server role"),
        (2, "is a member of db_owner"),
        (3, "is a member of db_ddladmin, so it can ALTER the tables the policies protect"),
        (4, "is a member of db_securityadmin"),
        (5, "holds ALTER ANY SECURITY POLICY"),
        (6, "holds CONTROL on the database"),
    ];

    /// <summary>
    /// Whether the connected principal can disable row-level security rather than merely be filtered
    /// by it. SQL Server has no read-time bypass, so this is the whole of the "can it get around the
    /// policies" question — and it is asked before any per-table check, because no per-table state can
    /// compensate for a principal that can turn the policy off.
    /// </summary>
    private async Task<IReadOnlyList<string>> InspectConnectedPrincipalAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();

        // Every membership and permission function is wrapped in ISNULL. They return NULL rather than
        // 0 for "not applicable" — IS_SRVROLEMEMBER does so on Azure SQL Database, where server roles
        // do not exist for a contained user — and a NULL read as a boolean throws here rather than
        // reporting. Azure SQL's nearest equivalent to sysadmin for a database principal is db_owner,
        // which is checked on its own line, so treating an unavailable server-role answer as "no"
        // loses nothing there. HAS_PERMS_BY_NAME(NULL, NULL, …) asks about the current database.
        //
        // Positional: every GetString and GetBoolean below indexes this SELECT by ordinal, and
        // PrincipalDisablers hard-codes those ordinals.
        command.CommandText =
            """
            SELECT
                USER_NAME()                                                                         AS principal_name,
                CONVERT(bit, ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0))                               AS is_sysadmin,
                CONVERT(bit, ISNULL(IS_ROLEMEMBER('db_owner'), 0))                                  AS is_db_owner,
                CONVERT(bit, ISNULL(IS_ROLEMEMBER('db_ddladmin'), 0))                               AS is_db_ddladmin,
                CONVERT(bit, ISNULL(IS_ROLEMEMBER('db_securityadmin'), 0))                          AS is_db_securityadmin,
                CONVERT(bit, ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY SECURITY POLICY'), 0)) AS can_alter_security_policy,
                CONVERT(bit, ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL'), 0))                   AS has_database_control
            """;

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // A missing row is a problem, not a pass. This SELECT has no FROM clause, so it always
            // returns exactly one row — there is no benign reading of its absence, and the
            // alternative is a security check with a branch that fails open, which is the one shape
            // it must never have. InspectAsync treats a missing row the same way, for the same reason.
            if (!await reader.ReadAsync(cancellationToken))
            {
                problems.Add(
                    "  the connected principal could not be identified, so whether it can disable " +
                    "row-level security could not be established.");

                return problems;
            }

            string principal = reader.GetString(0);

            foreach ((int ordinal, string capability) in PrincipalDisablers)
            {
                if (reader.GetBoolean(ordinal))
                {
                    problems.Add(
                        $"  the connected principal \"{principal}\" {capability}, so it can execute " +
                        "ALTER SECURITY POLICY ... WITH (STATE = OFF) and switch tenant isolation off " +
                        "in one statement. Migrations must run as a principal that is not the one " +
                        "serving requests, and the principal serving requests must hold no DDL or " +
                        "security-administration rights.");
                }
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

        // One round trip per table, straight from the catalog views.
        //
        // enabled_filter_predicates is the one that matters most, and it is deliberately not merely
        // "a predicate exists": a security policy can be created and then disabled
        // (ALTER SECURITY POLICY ... WITH (STATE = OFF)), which leaves every row in
        // sys.security_predicates exactly where it was while filtering nothing. Counting predicates
        // alone would pass on a table whose isolation is switched off — the SQL Server analogue of
        // the relrowsecurity-without-relforcerowsecurity hole the PostgreSQL version closed (D5).
        //
        // FILTER and BLOCK are counted separately because they are the two halves of the PostgreSQL
        // policy's USING and WITH CHECK: FILTER silently hides other tenants' rows from reads,
        // updates and deletes; BLOCK refuses a write that would create a row in, or move one into,
        // another tenant. A table carrying only the first reads safely and writes across tenants.
        //
        // can_alter_table goes LAST on purpose: every GetInt32 and GetBoolean below indexes this
        // SELECT positionally, so inserting anywhere else would silently re-point four existing checks.
        //
        // It asks HAS_PERMS_BY_NAME rather than comparing the schema's principal_id to the connected
        // user. The name comparison reads correctly and is wrong: ALTER on the table can arrive
        // through membership in db_ddladmin, through ownership of the schema, or through a direct
        // GRANT — each a plausible way to make a permission error go away, and nothing forbids them —
        // while the table's owner still reads as someone else. HAS_PERMS_BY_NAME answers the question
        // actually worth asking: not "is it named the owner" but "can it act as one".
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*)
                   FROM sys.columns c
                  WHERE c.object_id = t.object_id
                    AND c.name = 'tenant_id')                      AS has_tenant_id,
                (SELECT COUNT(*)
                   FROM sys.security_predicates sp
                  WHERE sp.target_object_id = t.object_id)         AS predicates,
                (SELECT COUNT(*)
                   FROM sys.security_predicates sp
                   JOIN sys.security_policies pol ON pol.object_id = sp.object_id
                  WHERE sp.target_object_id = t.object_id
                    AND sp.predicate_type_desc = 'FILTER'
                    AND pol.is_enabled = 1)                        AS enabled_filter_predicates,
                (SELECT COUNT(*)
                   FROM sys.security_predicates sp
                   JOIN sys.security_policies pol ON pol.object_id = sp.object_id
                  WHERE sp.target_object_id = t.object_id
                    AND sp.predicate_type_desc = 'BLOCK'
                    AND pol.is_enabled = 1)                        AS enabled_block_predicates,
                CONVERT(bit, ISNULL(HAS_PERMS_BY_NAME(
                    QUOTENAME(s.name) + '.' + QUOTENAME(t.name), 'OBJECT', 'ALTER'), 0)) AS can_alter_table
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table
            """;

        AddParameter(command, "@schema", schema);
        AddParameter(command, "@table", table);

        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                problems.Add($"  {schema}.{table}: the table does not exist.");
                return problems;
            }

            // 0: has_tenant_id
            if (reader.GetInt32(0) == 0)
            {
                problems.Add($"  {schema}.{table}: no tenant_id column.");
            }

            // 1: predicates
            if (reader.GetInt32(1) == 0)
            {
                problems.Add($"  {schema}.{table}: no row-level security predicate.");
            }

            // 2: enabled_filter_predicates
            if (reader.GetInt32(2) == 0)
            {
                problems.Add(
                    $"  {schema}.{table}: no ENABLED filter predicate, so reads are not restricted " +
                    "to the acting tenant. A disabled security policy leaves its predicates listed " +
                    "in the catalog while filtering nothing.");
            }

            // 3: enabled_block_predicates
            if (reader.GetInt32(3) == 0)
            {
                problems.Add(
                    $"  {schema}.{table}: no ENABLED block predicate, so a write can still create a " +
                    "row in, or move one into, another tenant even though reads are filtered.");
            }

            // 4: can_alter_table
            if (reader.GetBoolean(4))
            {
                problems.Add(
                    $"  {schema}.{table}: the connected principal can ALTER this table — directly, " +
                    "through schema ownership, or through db_ddladmin — so it can change the table " +
                    "out from under the policy that protects it. Migrations must run as a principal " +
                    "that is not the one serving requests.");
            }
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }

        return problems;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
