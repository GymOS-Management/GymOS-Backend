/*
    platform.fn_tenant_isolation

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/
CREATE FUNCTION platform.fn_tenant_isolation(@tenant_id uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
    RETURN SELECT 1 AS is_visible
    WHERE @tenant_id = CONVERT(uniqueidentifier, SESSION_CONTEXT(N'app.tenant_id'))
