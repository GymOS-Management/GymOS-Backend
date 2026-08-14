namespace GymOS.IntegrationTests.Tenancy;

/// <summary>The probe table, in the two shapes the isolation tests need.</summary>
internal static class TenantProbeSchema
{
    /// <summary>Correctly protected: RLS on, FORCED, one policy keyed on the session variable.</summary>
    public const string CreateSql =
        """
        CREATE TABLE IF NOT EXISTS platform.tenant_probe (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            note text NOT NULL
        );

        ALTER TABLE platform.tenant_probe ENABLE ROW LEVEL SECURITY;

        -- Without FORCE the owner — which is how this application connects — bypasses the policy
        -- below entirely, and every isolation test would pass while filtering nothing (D5).
        ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;

        DROP POLICY IF EXISTS tenant_probe_isolation ON platform.tenant_probe;

        -- NULLIF turns the empty string a platform-scope request writes into NULL, and
        -- `tenant_id = NULL` matches no rows: unset reads nothing rather than everything (D4).
        CREATE POLICY tenant_probe_isolation ON platform.tenant_probe
            USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
            WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
        """;

    /// <summary>The same table with no policy — the subject of the validator's negative test.</summary>
    public const string CreateWithoutPolicySql =
        """
        CREATE TABLE IF NOT EXISTS platform.tenant_probe (
            id uuid PRIMARY KEY,
            tenant_id uuid NOT NULL,
            note text NOT NULL
        );
        """;

    public const string DropSql = "DROP TABLE IF EXISTS platform.tenant_probe;";
}
