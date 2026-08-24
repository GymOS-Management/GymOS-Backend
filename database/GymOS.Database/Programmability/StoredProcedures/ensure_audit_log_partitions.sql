/*
    platform.ensure_audit_log_partitions

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/
CREATE PROCEDURE platform.ensure_audit_log_partitions
    @from_month   date,
    @months_ahead int,
    @created      int OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    IF @months_ahead < 0
        THROW 50001, 'months_ahead must not be negative.', 1;

    SET @created = 0;

    DECLARE @offset   int = 0;
    DECLARE @first    date = DATEFROMPARTS(YEAR(@from_month), MONTH(@from_month), 1);
    DECLARE @boundary datetimeoffset(7);
    DECLARE @exists   bit;

    WHILE @offset <= @months_ahead
    BEGIN
        SET @boundary = TODATETIMEOFFSET(
            CONVERT(datetime2(7), DATEADD(month, @offset, @first)), 0);

        SET @exists = platform.audit_log_boundary_exists(@boundary);

        IF @exists = 0
        BEGIN
            BEGIN TRY
                ALTER PARTITION SCHEME ps_audit_logs_monthly NEXT USED [PRIMARY];
                ALTER PARTITION FUNCTION pf_audit_logs_monthly() SPLIT RANGE (@boundary);

                SET @created = @created + 1;
            END TRY
            BEGIN CATCH
                -- Re-read rather than match an error number. If the boundary is there
                -- now, another instance split it between the check above and the ALTER
                -- — the one race this is allowed to lose, and both callers wanted the
                -- same outcome. If it is still missing, the failure was real.
                IF platform.audit_log_boundary_exists(@boundary) = 0
                    THROW;
            END CATCH
        END

        SET @offset = @offset + 1;
    END
END
