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

-- ── READ_COMMITTED_SNAPSHOT ──────────────────────────────────────────────────────────────────
-- Makes READ COMMITTED use row versioning instead of shared locks, so a reader never blocks a
-- writer and a writer never blocks a reader.
--
-- **This is the setting that makes the application's concurrency behave as it was written.** Every
-- handler in GymOS was designed against PostgreSQL, where MVCC gives non-blocking reads for free and
-- pessimistic locking is something you must ask for explicitly (SELECT ... FOR UPDATE). SQL Server's
-- default is the opposite: an ordinary SELECT takes shared locks, so two operations that never
-- contended before can now deadlock through a table neither of them locked on purpose.
--
-- Measured, not assumed. Without this, SetGymFeaturesCommand and SetPlanFeatureCommand deadlock when
-- they run concurrently: the gym write holds a deliberate shared lock on subscription_plans (its
-- FindByIdForShareAsync) and then READS plan_features, while the plan write holds plan_features and
-- wants subscription_plans. Neither reads a row the other has any business locking — the cycle exists
-- only because reads take locks. The integration suite has a test for exactly that race
-- (A_concurrent_plan_edit_cannot_make_the_gym_writes_own_answer_stale), and it returned a 500 until
-- this line existed.
--
-- The explicit hints keep working and keep meaning what they say: UPDLOCK and REPEATABLEREAD are
-- still pessimistic under RCSI, which is the point — the code asks for blocking exactly where it
-- wants it, and gets none where it does not.
--
-- Azure SQL Database has this ON by default, so enabling it here makes development and the
-- production target agree rather than diverge. Setting it needs exclusive access to the database,
-- which is why it happens now, before anything connects.
DECLARE @rcsi nvarchar(max) =
    N'ALTER DATABASE ' + QUOTENAME(N'$(GYMOS_DB)') + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE';
EXEC sys.sp_executesql @rcsi;
GO
