/*
    platform.audit_log_boundary_exists

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/
CREATE FUNCTION platform.audit_log_boundary_exists(@boundary datetimeoffset(7))
RETURNS bit
AS
BEGIN
    RETURN CASE WHEN EXISTS (
        SELECT 1
          FROM sys.partition_range_values prv
          JOIN sys.partition_functions pf ON pf.function_id = prv.function_id
         WHERE pf.name = N'pf_audit_logs_monthly'
           AND CONVERT(datetimeoffset(7), prv.value) = @boundary)
    THEN 1 ELSE 0 END;
END
