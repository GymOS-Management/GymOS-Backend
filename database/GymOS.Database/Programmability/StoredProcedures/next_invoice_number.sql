/*
    platform.next_invoice_number

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/
CREATE PROCEDURE platform.next_invoice_number
    @year   int,
    @number bigint OUTPUT
WITH EXECUTE AS OWNER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @sequence   sysname = N'invoice_number_seq_' + CONVERT(nvarchar(10), @year);
    DECLARE @qualified  nvarchar(300) = N'platform.' + QUOTENAME(@sequence);

    IF NOT EXISTS (SELECT 1 FROM sys.sequences s
                    JOIN sys.schemas sc ON sc.schema_id = s.schema_id
                   WHERE sc.name = N'platform' AND s.name = @sequence)
    BEGIN
        BEGIN TRY
            EXEC (N'CREATE SEQUENCE ' + @qualified +
                  N' AS bigint START WITH 1 INCREMENT BY 1 NO CACHE');
        END TRY
        BEGIN CATCH
            -- Only the lost-race case is survivable: if the sequence exists now,
            -- somebody else created it between the check and the CREATE. Anything
            -- else is a real failure and must reach the caller.
            IF NOT EXISTS (SELECT 1 FROM sys.sequences s
                            JOIN sys.schemas sc ON sc.schema_id = s.schema_id
                           WHERE sc.name = N'platform' AND s.name = @sequence)
                THROW;
        END CATCH
    END

    -- NO CACHE above, and it is load-bearing: a cached sequence loses its unissued
    -- block when the server restarts, which would leave visible gaps in a legally
    -- consecutive invoice series. Numbers are drawn one per invoice, so the cache
    -- would buy nothing worth that.
    DECLARE @next sql_variant;
    EXEC sys.sp_sequence_get_range @sequence_name = @qualified,
                                   @range_size = 1,
                                   @range_first_value = @next OUTPUT;

    SET @number = CONVERT(bigint, @next);
END
