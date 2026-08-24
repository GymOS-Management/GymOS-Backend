namespace GymOS.IntegrationTests.Tenancy;

/// <summary>The probe table, in the two shapes the isolation tests need.</summary>
/// <remarks>
/// <para><b>The predicate is not defined here.</b> It is
/// <c>platform.fn_tenant_isolation</c>, created by the <c>RuntimeDatabaseProgrammability</c>
/// migration, and the policy below only points at it. The PostgreSQL original spelled its
/// <c>USING</c>/<c>WITH CHECK</c> expression out inline, which meant this suite proved a predicate
/// that existed nowhere else — a probe that validates a *different* expression from the one
/// production will use validates nothing. Referencing the shipped function is what makes these tests
/// evidence about the real mechanism.</para>
/// <para><b>Each DDL statement is wrapped in <c>EXEC</c>.</b> <c>CREATE SECURITY POLICY</c> and
/// <c>CREATE TABLE</c> must each be the first statement in their batch, and these constants are sent
/// as a single command with no batch separator (<c>GO</c> is a client directive, not T-SQL, and
/// SqlClient does not understand it). <c>EXEC</c> gives each statement its own batch without needing
/// the caller to split the string.</para>
/// </remarks>
internal static class TenantProbeSchema
{
    /// <summary>
    /// Correctly protected: an enabled security policy with both a FILTER and a BLOCK predicate.
    /// </summary>
    /// <remarks>
    /// <para><b>There is no <c>FORCE</c> here, and none is missing.</b> PostgreSQL exempts a table's
    /// owner from its own policies unless <c>FORCE ROW LEVEL SECURITY</c> is set, so the original had
    /// to set it and the validator had to assert it (D5). SQL Server has no owner exemption at all: a
    /// security policy filters every principal that queries the table, including the schema owner,
    /// <c>dbo</c> and <c>sysadmin</c>. The property <c>FORCE</c> bought is the default here, which is
    /// why <c>TheOwnerDoesNotBypassThePolicy</c> still passes and why nothing in this file turns it on.
    /// </para>
    /// <para><b>FILTER and BLOCK are the two halves of <c>USING</c> and <c>WITH CHECK</c>.</b> FILTER
    /// hides other tenants' rows from reads, updates and deletes; BLOCK refuses a write that would
    /// create a row in, or move one into, another tenant. Unlike PostgreSQL — where omitting
    /// <c>WITH CHECK</c> silently falls back to the <c>USING</c> expression — leaving the BLOCK
    /// predicate out here would leave writes entirely unpoliced, which is why the validator counts the
    /// two separately.</para>
    /// </remarks>
    public const string CreateSql =
        """
        IF OBJECT_ID('platform.tenant_probe', 'U') IS NULL
            EXEC('
                CREATE TABLE platform.tenant_probe (
                    id        uniqueidentifier NOT NULL CONSTRAINT pk_tenant_probe PRIMARY KEY,
                    tenant_id uniqueidentifier NOT NULL,
                    note      nvarchar(400)    NOT NULL
                )');

        IF OBJECT_ID('platform.tenant_probe_isolation', 'SP') IS NOT NULL
            DROP SECURITY POLICY platform.tenant_probe_isolation;

        EXEC('
            CREATE SECURITY POLICY platform.tenant_probe_isolation
                ADD FILTER PREDICATE platform.fn_tenant_isolation(tenant_id)
                    ON platform.tenant_probe,
                ADD BLOCK PREDICATE platform.fn_tenant_isolation(tenant_id)
                    ON platform.tenant_probe AFTER INSERT
                WITH (STATE = ON)');
        """;

    /// <summary>The same table with no policy — the subject of the validator's negative test.</summary>
    public const string CreateWithoutPolicySql =
        """
        IF OBJECT_ID('platform.tenant_probe', 'U') IS NULL
            EXEC('
                CREATE TABLE platform.tenant_probe (
                    id        uniqueidentifier NOT NULL CONSTRAINT pk_tenant_probe PRIMARY KEY,
                    tenant_id uniqueidentifier NOT NULL,
                    note      nvarchar(400)    NOT NULL
                )');
        """;

    /// <summary>
    /// Drops both, policy first.
    /// </summary>
    /// <remarks>
    /// Order is not stylistic. A security policy's predicate function is schema-bound to the table it
    /// filters, so SQL Server refuses to drop the table while the policy still references it — the
    /// reverse order fails with "cannot DROP TABLE because it is being referenced by object
    /// 'tenant_probe_isolation'".
    /// </remarks>
    public const string DropSql =
        """
        IF OBJECT_ID('platform.tenant_probe_isolation', 'SP') IS NOT NULL
            DROP SECURITY POLICY platform.tenant_probe_isolation;

        IF OBJECT_ID('platform.tenant_probe', 'U') IS NOT NULL
            DROP TABLE platform.tenant_probe;
        """;
}
