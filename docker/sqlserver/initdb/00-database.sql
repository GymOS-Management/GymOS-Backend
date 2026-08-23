-- Creates the GymOS database. Runs once, before migrations, as the sa login.
--
-- ── Collation ────────────────────────────────────────────────────────────────────────────────
-- Latin1_General_100_CI_AS: case-INsensitive, accent-sensitive. Fixed at CREATE DATABASE because
-- changing it afterwards means rebuilding every character column and index in the database.
--
-- Case-insensitive is a deliberate change from the PostgreSQL cluster, which was initialised with
-- `lc-collate=C` and therefore compared text byte-for-byte. Two consequences, both wanted:
--
--   * Free-text search works. Those queries used ILIKE, which SQL Server does not have; a plain
--     LIKE against a CI collation is the equivalent, and it is the collation doing the work.
--     See BuildingBlocks.Infrastructure.Persistence.SearchPatterns.
--   * Unique indexes on text become case-insensitive. Every such column is normalised before it
--     is stored — gym slugs and emails lowercased, plan codes uppercased — so no value the
--     application writes can collide differently than it did before. For data written out of
--     band it is stricter, which is the safe direction.
--
-- Accent-SENSITIVE, not AI: 'Müller' and 'Muller' are different gyms and different people, and a
-- platform operating in Cyprus handles Greek and accented Latin names as a matter of course.
IF DB_ID('$(GYMOS_DB)') IS NULL
BEGIN
    DECLARE @sql nvarchar(max) =
        N'CREATE DATABASE ' + QUOTENAME(N'$(GYMOS_DB)') + N' COLLATE Latin1_General_100_CI_AS';
    EXEC sys.sp_executesql @sql;
END
GO
