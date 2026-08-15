using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes the two SQL functions that issue DDL at runtime executable by a role that holds no DDL
    /// rights of its own.
    ///
    /// <para><b>Why this exists.</b> The three-role increment took DDL away from the process that
    /// serves requests: <c>gymos_app</c> owns nothing, holds no <c>CREATE</c> on <c>platform</c>, and
    /// migrations run as <c>gymos_migrator</c> in a one-shot container. Two functions installed by
    /// earlier migrations issue DDL as the <i>calling</i> role, because plpgsql defaults to
    /// <c>SECURITY INVOKER</c> when no clause says otherwise:</para>
    /// <list type="bullet">
    /// <item><c>platform.next_invoice_number</c> (<c>AddInvoices</c>) creates
    /// <c>platform.invoice_number_seq_&lt;year&gt;</c> on the first invoice of any year. As
    /// <c>gymos_app</c> that is <c>42501 permission denied for schema platform</c>, and the function's
    /// own handler catches only <c>duplicate_object</c>/<c>unique_violation</c>, so it surfaces as a
    /// 500 on invoice issuance — the first invoice against a fresh database could not be issued at
    /// all.</item>
    /// <item><c>platform.ensure_audit_log_partitions</c> (<c>PartitionAuditLogs</c>) creates next
    /// month's partition. The same privilege error was <i>swallowed</i> there by
    /// <c>EXCEPTION WHEN others</c>, so the function returned 0 and the maintainer logged
    /// Information "0 created" — audit rows would have silently accumulated in
    /// <c>audit_logs_default</c> forever once the migration's built-in lookahead elapsed.</item>
    /// </list>
    ///
    /// <para><b>What changes.</b> Both become <c>SECURITY DEFINER</c>, so they execute as their owner
    /// (<c>gymos_migrator</c>) rather than the caller, which is what gives them the <c>CREATE</c> they
    /// need. That is a privilege elevation and is treated as one:</para>
    /// <list type="bullet">
    /// <item><b>The search path is pinned</b> (<c>SET search_path = pg_catalog, pg_temp</c>). A
    /// <c>SECURITY DEFINER</c> function without a pinned path is a privilege-escalation vector: the
    /// caller controls <c>search_path</c>, so it can point the elevated function at a schema it
    /// writes and have the function call its code as the owner. Every identifier in both bodies is
    /// already schema-qualified, so <c>pg_catalog</c> plus an explicit trailing <c>pg_temp</c> — the
    /// form PostgreSQL's own documentation recommends, because <c>pg_temp</c> is otherwise searched
    /// ahead of everything for relation names — resolves all of them and nothing else.</item>
    /// <item><b>Execution is restricted.</b> <c>CREATE FUNCTION</c> grants <c>EXECUTE</c> to
    /// <c>PUBLIC</c> by default, which on a <c>SECURITY DEFINER</c> function means every role in the
    /// database may run DDL as the schema owner. That default is revoked and granted back explicitly
    /// to <c>gymos_app</c>. The owner keeps <c>EXECUTE</c> regardless — revoking from <c>PUBLIC</c>
    /// does not touch it — which is what keeps the single-role integration harness working.</item>
    /// </list>
    ///
    /// <para><b>And the swallow is narrowed.</b> <c>ensure_audit_log_partitions</c> caught
    /// <c>others</c> and downgraded it to a warning; that is how a privilege error came to look like
    /// routine maintenance. It now catches only the two conditions its own comment describes —
    /// <c>check_violation</c> (rows already in the DEFAULT partition for the range, which PostgreSQL
    /// will not split) and <c>duplicate_table</c> (the creation race the catalog check above it does
    /// not close). Anything else, <c>insufficient_privilege</c> included, propagates to
    /// <c>AuditLogPartitionMaintainer</c>, whose Error path exists precisely for it.</para>
    ///
    /// <para><b>Why a new migration rather than editing the applied ones.</b> Editing
    /// <c>AddInvoices</c> or <c>PartitionAuditLogs</c> would fix a database created from scratch after
    /// this commit and no other — every database that already ran them keeps the broken definitions.
    /// <c>CREATE OR REPLACE FUNCTION</c> in a new migration fixes both.</para>
    ///
    /// <para><b>Deployments whose application role is not named <c>gymos_app</c></b> must grant
    /// <c>EXECUTE</c> on both functions to whatever role serves requests; the grant below is guarded
    /// on the role existing, so it is a no-op where it does not. That is the case in the integration
    /// harness (see <c>TenantIsolationTests.GrantRuntimeFunctionsSql</c>) and it is written out in
    /// <c>docs/runbooks/managed-postgres-roles.md</c> for a managed instance.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class SecureRuntimeDdlFunctions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // ── platform.next_invoice_number ────────────────────────────────────────────────
            // Body unchanged from AddInvoices apart from the two clauses on the header. The
            // duplicate_object/unique_violation handler stays exactly as it was: it covers the
            // first-of-the-year creation race, which SECURITY DEFINER does nothing about.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.next_invoice_number(p_year integer)
                RETURNS bigint
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
                AS $function$
                DECLARE
                    sequence_name text := format('invoice_number_seq_%s', p_year);
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_sequences
                        WHERE schemaname = 'platform' AND sequencename = sequence_name)
                    THEN
                        BEGIN
                            EXECUTE format('CREATE SEQUENCE IF NOT EXISTS platform.%I', sequence_name);
                        EXCEPTION WHEN duplicate_object OR unique_violation THEN
                            -- Two first-of-the-year issuers can both pass the check above and race
                            -- the CREATE; IF NOT EXISTS does not close that window (PostgreSQL can
                            -- still surface it as a catalog unique violation). Whoever lost the race
                            -- uses the winner's sequence, which is the outcome both wanted.
                            NULL;
                        END;
                    END IF;
                    RETURN nextval(format('platform.%I', sequence_name));
                END;
                $function$;
                """);

            // ── platform.ensure_audit_log_partitions ────────────────────────────────────────
            // Same two header clauses, plus the narrowed handler. `CREATE OR REPLACE` keeps the
            // COMMENT the original migration attached, so it is not restated here.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.ensure_audit_log_partitions(
                    from_month date,
                    months_ahead integer
                ) RETURNS integer
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, pg_temp
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
                        EXCEPTION WHEN check_violation OR duplicate_table THEN
                            -- The two conditions maintenance can legitimately lose to, and only
                            -- those. check_violation: rows already sit in the DEFAULT partition for
                            -- this range, which PostgreSQL will not split automatically.
                            -- duplicate_table: another session created the partition between the
                            -- catalog check above and this statement, the same race the invoice
                            -- numbering function tolerates. Both mean writes keep succeeding into
                            -- DEFAULT (or into the partition the winner made), so no audit row is
                            -- lost or refused because maintenance fell behind.
                            --
                            -- Everything else propagates. This used to be `WHEN others`, and that is
                            -- how insufficient_privilege — the function being unable to create a
                            -- partition at all — arrived at the caller as "0 created" at
                            -- Information level. A control that reports a permission failure as
                            -- routine housekeeping is worse than no control.
                            RAISE WARNING 'audit_logs partition % not created: %', partition_name, SQLERRM;
                        END;
                    END LOOP;

                    RETURN created;
                END;
                $function$;
                """);

            // ── Who may run them ────────────────────────────────────────────────────────────
            // CREATE FUNCTION grants EXECUTE to PUBLIC. On a SECURITY DEFINER function that hands
            // every role in the database the owner's DDL rights through these two entry points, so
            // the default is revoked and granted back to the one role that needs it.
            //
            // Guarded on the role existing rather than issued unconditionally: this same migration
            // runs against the integration harness's containers, where the application role is named
            // something else (or the single test role owns everything and keeps EXECUTE as owner),
            // and a bare GRANT would fail the migration there with `role "gymos_app" does not exist`.
            // A deployment whose serving role is named differently grants explicitly — see the
            // runbook.
            migrationBuilder.Sql(
                """
                REVOKE ALL ON FUNCTION platform.next_invoice_number(integer) FROM PUBLIC;
                REVOKE ALL ON FUNCTION platform.ensure_audit_log_partitions(date, integer) FROM PUBLIC;

                DO $grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'gymos_app') THEN
                        EXECUTE 'GRANT EXECUTE ON FUNCTION platform.next_invoice_number(integer) TO gymos_app';
                        EXECUTE 'GRANT EXECUTE ON FUNCTION platform.ensure_audit_log_partitions(date, integer) TO gymos_app';
                    END IF;
                END
                $grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Restores both functions exactly as AddInvoices and PartitionAuditLogs left them —
            // SECURITY INVOKER, unpinned search path, `WHEN others` — and returns EXECUTE to PUBLIC,
            // which is what CREATE FUNCTION had granted. Reverting this migration therefore reverts
            // to a deployment where the API cannot issue an invoice or create an audit partition;
            // that is what "revert" means here, and it is the state the next migration up fixes.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION platform.next_invoice_number(p_year integer)
                RETURNS bigint
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    sequence_name text := format('invoice_number_seq_%s', p_year);
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_sequences
                        WHERE schemaname = 'platform' AND sequencename = sequence_name)
                    THEN
                        BEGIN
                            EXECUTE format('CREATE SEQUENCE IF NOT EXISTS platform.%I', sequence_name);
                        EXCEPTION WHEN duplicate_object OR unique_violation THEN
                            NULL;
                        END;
                    END IF;
                    RETURN nextval(format('platform.%I', sequence_name));
                END;
                $function$;
                """);

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
                            RAISE WARNING 'audit_logs partition % not created: %', partition_name, SQLERRM;
                        END;
                    END LOOP;

                    RETURN created;
                END;
                $function$;
                """);

            migrationBuilder.Sql(
                """
                GRANT EXECUTE ON FUNCTION platform.next_invoice_number(integer) TO PUBLIC;
                GRANT EXECUTE ON FUNCTION platform.ensure_audit_log_partitions(date, integer) TO PUBLIC;
                """);
        }
    }
}
