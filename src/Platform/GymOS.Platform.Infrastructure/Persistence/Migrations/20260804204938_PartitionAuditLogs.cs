using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Converts <c>platform.audit_logs</c> to a monthly range-partitioned table
    /// (ARCHITECTURE.md §2.6, AD-7).
    ///
    /// <para><b>Why this migration is hand-authored SQL.</b> EF Core cannot express
    /// <c>PARTITION BY RANGE</c>, and PostgreSQL cannot add a partition key to an existing
    /// table with <c>ALTER TABLE</c> — the table has to be recreated. The scaffolded migration
    /// for this model change contained only the primary-key widening, which is the part EF could
    /// see. All partitioning DDL is therefore written out below and confined to this file plus
    /// one SQL function; no raw SQL leaks into repositories, queries, or handlers.</para>
    ///
    /// <para><b>Migration strategy (rename → create → copy → drop).</b> The existing table is
    /// moved aside rather than dropped, its rows are copied into the partitioned table, and only
    /// then is it dropped — so this is non-destructive and behaves identically whether the table
    /// is empty (today) or holds production rows (later). It runs inside the migration
    /// transaction: a failure at any step rolls back with the original table intact.</para>
    ///
    /// <para><b>Why now.</b> Converting an empty table costs nothing. Converting a large
    /// append-only audit table later means copying every row while holding a lock — which is
    /// precisely the expensive production migration this milestone exists to avoid.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class PartitionAuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // ── 1. Move the existing table aside ────────────────────────────────────────
            // Its constraint and indexes are renamed too: those names are canonical on the new
            // table, and PostgreSQL would reject the duplicates.
            migrationBuilder.Sql(
                """
                ALTER TABLE platform.audit_logs RENAME TO audit_logs_legacy;
                ALTER TABLE platform.audit_logs_legacy RENAME CONSTRAINT pk_audit_logs TO pk_audit_logs_legacy;
                ALTER INDEX platform.ix_audit_logs_action RENAME TO ix_audit_logs_legacy_action;
                ALTER INDEX platform.ix_audit_logs_created_at_utc RENAME TO ix_audit_logs_legacy_created_at;
                ALTER INDEX platform.ix_audit_logs_entity_type_entity_id RENAME TO ix_audit_logs_legacy_entity;
                ALTER INDEX platform.ix_audit_logs_user_id RENAME TO ix_audit_logs_legacy_user_id;
                """);

            // ── 2. The partitioned parent ───────────────────────────────────────────────
            // Columns mirror the EF model exactly (AuditLogConfiguration remains the source of
            // truth for the shape; this is only its DDL).
            migrationBuilder.Sql(
                """
                CREATE TABLE platform.audit_logs (
                    id uuid NOT NULL,
                    user_id uuid NULL,
                    user_email character varying(320) NULL,
                    ip_address character varying(45) NULL,
                    user_agent character varying(512) NULL,
                    action character varying(100) NOT NULL,
                    entity_type character varying(100) NULL,
                    entity_id uuid NULL,
                    old_values jsonb NULL,
                    new_values jsonb NULL,
                    correlation_id uuid NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT pk_audit_logs PRIMARY KEY (id, created_at_utc)
                ) PARTITION BY RANGE (created_at_utc);
                """);

            // Declared on the parent: PostgreSQL 11+ creates a matching index on every existing
            // partition and on each one added later, so new months need no index DDL at all.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_logs_created_at_utc ON platform.audit_logs (created_at_utc DESC);
                CREATE INDEX ix_audit_logs_entity_type_entity_id ON platform.audit_logs (entity_type, entity_id);
                CREATE INDEX ix_audit_logs_user_id ON platform.audit_logs (user_id);
                CREATE INDEX ix_audit_logs_action ON platform.audit_logs (action);
                """);

            // ── 3. Partition maintenance function ───────────────────────────────────────
            // The one piece of SQL that outlives this migration. Idempotent, so it is safe to
            // call from anywhere: the startup maintainer calls it today and the recurring
            // retention job will call the same function when the job runner lands (AD-23).
            //
            // Every bound is computed in UTC explicitly. Deriving them from the session TimeZone
            // would let partition edges move with client configuration, and a row filed under the
            // wrong month stays invisible until someone detaches that partition and loses it.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.ensure_audit_log_partitions(
                    from_month date,
                    months_ahead integer
                ) RETURNS integer
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    target_month date;
                    partition_name text;
                    range_start timestamptz;
                    range_end timestamptz;
                    created integer := 0;
                BEGIN
                    IF months_ahead < 0 THEN
                        RAISE EXCEPTION 'months_ahead must not be negative';
                    END IF;

                    FOR offset_months IN 0..months_ahead LOOP
                        target_month := (date_trunc('month', from_month::timestamp)
                            + make_interval(months => offset_months))::date;
                        partition_name := 'audit_logs_' || to_char(target_month, 'YYYY_MM');
                        range_start := timezone('UTC', target_month::timestamp);
                        range_end := timezone('UTC', (target_month + interval '1 month')::timestamp);

                        CONTINUE WHEN EXISTS (
                            SELECT 1
                            FROM pg_class c
                            JOIN pg_namespace n ON n.oid = c.relnamespace
                            WHERE n.nspname = 'platform' AND c.relname = partition_name
                        );

                        BEGIN
                            EXECUTE format(
                                'CREATE TABLE platform.%I PARTITION OF platform.audit_logs '
                                'FOR VALUES FROM (%L) TO (%L)',
                                partition_name, range_start, range_end);
                            created := created + 1;
                        EXCEPTION WHEN others THEN
                            -- The realistic cause is rows already sitting in the DEFAULT partition
                            -- for this range, which PostgreSQL will not split automatically. Warn
                            -- and carry on: writes keep succeeding into DEFAULT, so no audit row is
                            -- ever lost or refused because maintenance fell behind.
                            RAISE WARNING 'audit_logs partition % not created: %', partition_name, SQLERRM;
                        END;
                    END LOOP;

                    RETURN created;
                END;
                $function$;

                COMMENT ON FUNCTION platform.ensure_audit_log_partitions(date, integer) IS
                    'Creates missing monthly partitions of platform.audit_logs, from from_month '
                    'inclusive through months_ahead months later. Idempotent; returns how many '
                    'were created.';
                """);

            // ── 4. Partitions for the existing rows, plus a lookahead ──────────────────
            // Created before the copy so no row lands in DEFAULT during the migration itself.
            migrationBuilder.Sql(
                """
                DO $migration$
                DECLARE
                    oldest date;
                    newest date;
                    span integer;
                BEGIN
                    SELECT date_trunc('month', min(created_at_utc) AT TIME ZONE 'UTC')::date,
                           date_trunc('month', max(created_at_utc) AT TIME ZONE 'UTC')::date
                      INTO oldest, newest
                      FROM platform.audit_logs_legacy;

                    -- Empty table (the expected case today): start at the current month.
                    oldest := coalesce(oldest, date_trunc('month', now() AT TIME ZONE 'UTC')::date);
                    newest := greatest(
                        coalesce(newest, oldest),
                        date_trunc('month', now() AT TIME ZONE 'UTC')::date);

                    span := (extract(year from newest)::integer - extract(year from oldest)::integer) * 12
                          + (extract(month from newest)::integer - extract(month from oldest)::integer);

                    -- Three months of headroom, so writes stay correctly partitioned even if the
                    -- application is never restarted and no maintenance runs for a quarter.
                    PERFORM platform.ensure_audit_log_partitions(oldest, span + 3);
                END $migration$;
                """);

            // ── 5. DEFAULT partition — the safety net ───────────────────────────────────
            // Audit rows are written inside the transaction of the operation they describe, so a
            // rejected audit insert would fail that operation. DEFAULT makes that impossible: an
            // unanticipated month becomes a warning and a misfiled row, never a refused write.
            // Consequence for the retention job: prune DEFAULT with DELETE, not DETACH.
            migrationBuilder.Sql(
                """
                CREATE TABLE platform.audit_logs_default PARTITION OF platform.audit_logs DEFAULT;
                """);

            // ── 6. Copy the rows, then drop the old table ──────────────────────────────
            migrationBuilder.Sql(
                """
                INSERT INTO platform.audit_logs (
                    id, user_id, user_email, ip_address, user_agent, action,
                    entity_type, entity_id, old_values, new_values, correlation_id, created_at_utc)
                SELECT
                    id, user_id, user_email, ip_address, user_agent, action,
                    entity_type, entity_id, old_values, new_values, correlation_id, created_at_utc
                FROM platform.audit_logs_legacy;

                DROP TABLE platform.audit_logs_legacy;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Reverses by the same route: build a plain table, copy every row back, then drop the
            // partitioned one. Dropping the parent drops its partitions, so the copy comes first.
            migrationBuilder.Sql(
                """
                CREATE TABLE platform.audit_logs_unpartitioned (
                    id uuid NOT NULL,
                    user_id uuid NULL,
                    user_email character varying(320) NULL,
                    ip_address character varying(45) NULL,
                    user_agent character varying(512) NULL,
                    action character varying(100) NOT NULL,
                    entity_type character varying(100) NULL,
                    entity_id uuid NULL,
                    old_values jsonb NULL,
                    new_values jsonb NULL,
                    correlation_id uuid NULL,
                    created_at_utc timestamp with time zone NOT NULL,
                    CONSTRAINT pk_audit_logs_unpartitioned PRIMARY KEY (id)
                );

                INSERT INTO platform.audit_logs_unpartitioned (
                    id, user_id, user_email, ip_address, user_agent, action,
                    entity_type, entity_id, old_values, new_values, correlation_id, created_at_utc)
                SELECT
                    id, user_id, user_email, ip_address, user_agent, action,
                    entity_type, entity_id, old_values, new_values, correlation_id, created_at_utc
                FROM platform.audit_logs;

                DROP TABLE platform.audit_logs;
                DROP FUNCTION IF EXISTS platform.ensure_audit_log_partitions(date, integer);

                ALTER TABLE platform.audit_logs_unpartitioned RENAME TO audit_logs;
                ALTER TABLE platform.audit_logs RENAME CONSTRAINT pk_audit_logs_unpartitioned TO pk_audit_logs;

                CREATE INDEX ix_audit_logs_created_at_utc ON platform.audit_logs (created_at_utc DESC);
                CREATE INDEX ix_audit_logs_entity_type_entity_id ON platform.audit_logs (entity_type, entity_id);
                CREATE INDEX ix_audit_logs_user_id ON platform.audit_logs (user_id);
                CREATE INDEX ix_audit_logs_action ON platform.audit_logs (action);
                """);
        }
    }
}
