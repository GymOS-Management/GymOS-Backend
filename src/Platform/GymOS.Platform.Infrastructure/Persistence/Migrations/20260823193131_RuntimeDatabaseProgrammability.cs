using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Everything the GymOS schema needs that EF Core cannot express: monthly range partitioning of
    /// <c>platform.audit_logs</c>, the per-year invoice-number sequence, and the row-level security
    /// predicate R14's tenant isolation is built on.
    ///
    /// <para><b>Why one migration rather than three.</b> On PostgreSQL these arrived separately —
    /// <c>PartitionAuditLogs</c>, <c>AddInvoices</c>, and the probe schema the tenancy suite created by
    /// hand — and a fourth (<c>SecureRuntimeDdlFunctions</c>) later went back and fixed the privileges
    /// on the first two. The SQL Server migration history starts from scratch, so there is no applied
    /// database to preserve and no reason to reproduce that sequence: the corrected end state is what
    /// gets written, once.</para>
    ///
    /// <para><b>Why it is hand-authored SQL.</b> The same reason it was under PostgreSQL. EF Core has
    /// no model concept for a partition function, a stored procedure, or a security policy, and the
    /// scaffolded migration for this model change contained none of it. All of it is confined to this
    /// file plus the three modules it creates; no raw SQL leaks into repositories, queries, or
    /// handlers.</para>
    ///
    /// <para><b>The privilege model, which is the part most changed by the move.</b> Two principals,
    /// as before: <c>gymos_migrator</c> owns the schemas and runs DDL, <c>gymos_app</c> owns nothing
    /// and serves requests. Two of the modules below issue DDL at <i>runtime</i> on the application's
    /// behalf — a new year's invoice sequence, next month's partition boundary — so they are created
    /// <c>WITH EXECUTE AS OWNER</c>, which is SQL Server's counterpart to the <c>SECURITY DEFINER</c>
    /// the PostgreSQL functions ended up needing. The owner is the schema owner, so they execute with
    /// the migrator's rights however the caller connected. Unlike <c>SECURITY DEFINER</c> there is no
    /// search-path hazard to pin shut: SQL Server resolves unqualified names inside a module against
    /// the module's own schema, not the caller's, so the escalation vector that made
    /// <c>SET search_path = pg_catalog, pg_temp</c> mandatory does not exist here. Every name is
    /// schema-qualified anyway.</para>
    ///
    /// <para><b>This migration grants nothing, and that is deliberate.</b> <c>CREATE PROCEDURE</c>
    /// grants EXECUTE to nobody on SQL Server — the opposite of PostgreSQL's <c>CREATE FUNCTION</c>,
    /// which grants to <c>PUBLIC</c> and had to be revoked by a later migration. So there is nothing
    /// to revoke here; the application principal's EXECUTE arrives instead from
    /// <c>GRANT EXECUTE ON SCHEMA::platform</c>, issued once at bootstrap before any migration runs.
    /// That is the direct counterpart of the <c>ALTER DEFAULT PRIVILEGES</c> lines the PostgreSQL
    /// initdb script used for tables and sequences: a standing grant that covers every object a future
    /// migration adds to the schema, so shipping a new procedure needs no privilege change.</para>
    /// <para>The grant cannot live in this migration even though the objects do, and the reason is
    /// worth recording because the first attempt did exactly that and failed silently.
    /// <c>sys.database_principals</c> is a security catalog view: it returns only the principals the
    /// caller may see, and <c>gymos_migrator</c> — <c>db_ddladmin</c>, deliberately not
    /// <c>db_securityadmin</c> — cannot see <c>gymos_app</c> in it at all. A guard of the form
    /// <c>IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'gymos_app')</c> is therefore
    /// false when the migrator runs it, on a database where that user plainly exists, and the GRANT
    /// inside it is skipped without error. The migrator can perform the grant; it just cannot ask
    /// whether to. Naming the principal is the bootstrap's job, which is where the name is known
    /// anyway — the integration harness uses different names and grants its own.</para>
    /// <para>See <c>docker/sqlserver/initdb/10-principals.sql</c> and
    /// <c>docs/runbooks/managed-sqlserver-roles.md</c>.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class RuntimeDatabaseProgrammability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            CreateIndexesAndConstraintsEfCannotExpress(migrationBuilder);
            CreateTenantIsolationPredicate(migrationBuilder);
            CreateInvoiceNumbering(migrationBuilder);
            PartitionAuditLogs(migrationBuilder);
            CreatePartitionMaintenance(migrationBuilder);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 0. Indexes and constraints the EF model cannot carry
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Three unique indexes and one CHECK constraint that the scaffolded migration does not emit,
        /// because the model has no way to declare them.
        /// </summary>
        /// <remarks>
        /// <para>Each of these existed under PostgreSQL as raw SQL inside the migration that added its
        /// table — <c>AddSubscriptionPlans</c>, <c>AddIdempotencyRecords</c>, <c>AddPlatformStats</c>
        /// and <c>AddInvoices</c>. Rebuilding the history from the model would have silently dropped
        /// all four, since none of them is in the model to be scaffolded from. They are collected here
        /// rather than scattered, because there is now one migration that owns everything hand-written.
        /// </para>
        /// <para><b>Two data backfills from those migrations are deliberately NOT carried over.</b>
        /// <c>AddPeriodAnchor</c> set <c>period_anchor_utc</c> from <c>current_period_start_utc</c> and
        /// <c>AddSubscriptionExpiredAt</c> set <c>expired_at_utc</c> for already-expired subscriptions.
        /// Both existed to repair rows that predated a new column; on a database created from this
        /// history there are no such rows, and running them would be a no-op against an empty table.
        /// They are not lost functionality — they were one-time repairs, and their outcome is now the
        /// starting state.</para>
        /// </remarks>
        private static void CreateIndexesAndConstraintsEfCannotExpress(MigrationBuilder migrationBuilder)
        {
            // EF cannot declare a unique index over a complex property's column (price_currency
            // belongs to the Money complex property), so this stays hand-written — unchanged from the
            // PostgreSQL original apart from the schema qualifier syntax.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ix_plan_prices_plan_cycle_currency
                    ON platform.plan_prices (plan_id, billing_cycle, price_currency);
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_daily_platform_stats_mrr_currency
                    ON platform.daily_platform_stats_mrr (stats_id, amount_currency);
                """);

            // The claim index, and the one that got materially simpler.
            //
            // The PostgreSQL version indexed an EXPRESSION —
            // `(COALESCE(tenant_id, '00000000-...'::uuid)), operation_key, idempotency_key` — for one
            // reason: PostgreSQL unique indexes treat two NULLs as distinct, so a plain index over a
            // nullable tenant_id would have let unlimited platform-scope claims share an idempotency
            // key. Substituting a sentinel UUID for NULL forced them to collide.
            //
            // SQL Server treats NULLs as EQUAL in a unique index, so the collision the COALESCE was
            // manufacturing is simply the default. The sentinel is not merely unnecessary here — it
            // would have to be a PERSISTED computed column, since SQL Server cannot index a bare
            // expression, adding a column to the table for a semantic the engine already provides.
            //
            // `HasFilter` has no counterpart to worry about either: this is raw DDL, so EF's habit of
            // adding `WHERE [tenant_id] IS NOT NULL` to unique indexes over nullable columns — the
            // trap RoleConfiguration documents — does not apply. Written out, it indexes every row.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_idempotency_records_claim
                    ON platform.idempotency_records (tenant_id, operation_key, idempotency_key);
                """);

            // Money's three columns on an invoice must agree, and the aggregate is not the only thing
            // that can write them — which is the point: this holds the invariant against anything that
            // is not the aggregate.
            migrationBuilder.Sql(
                """
                ALTER TABLE platform.invoices
                    ADD CONSTRAINT ck_invoices_single_currency
                    CHECK (subtotal_currency = tax_currency AND tax_currency = total_currency);
                """);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 1. Tenant isolation predicate (R14)
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The inline table-valued function every tenant-scoped table's security policy uses.
        ///
        /// <para><b>No production table is tenant-scoped yet</b>, exactly as under PostgreSQL: R14
        /// installed the mechanism and proved it against a table that exists only in the integration
        /// suite (<c>TenantProbeSchema</c>). The predicate lives here rather than in that suite so the
        /// production policies, when they arrive, and the probe's policy are the same code — a probe
        /// that proved a <i>different</i> predicate would prove nothing.</para>
        ///
        /// <para><b>It reads <c>SESSION_CONTEXT</c>, which <c>TenantSessionInterceptor</c> writes on
        /// every connection open.</b> That replaces PostgreSQL's <c>current_setting('app.tenant_id',
        /// true)</c> and the <c>NULLIF(…, '')::uuid</c> dance around it: session context holds a typed
        /// <c>uniqueidentifier</c>, so there is no empty string to convert and no conversion that could
        /// throw. Platform scope writes NULL, and <c>@tenant_id = NULL</c> is never true, so a
        /// tenant-scoped table matches no rows under platform scope — fail closed (D4), the same
        /// outcome by a simpler route.</para>
        ///
        /// <para><b>SCHEMABINDING is required</b>, not decorative: SQL Server refuses to use a function
        /// as a security predicate without it. It also has a useful consequence — the function cannot
        /// be altered or dropped while a policy depends on it.</para>
        /// </summary>
        private static void CreateTenantIsolationPredicate(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
                CREATE FUNCTION platform.fn_tenant_isolation(@tenant_id uniqueidentifier)
                RETURNS TABLE
                WITH SCHEMABINDING
                AS
                    RETURN SELECT 1 AS is_visible
                    WHERE @tenant_id = CONVERT(uniqueidentifier, SESSION_CONTEXT(N'app.tenant_id'));
                """);

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 2. Invoice numbering
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Hands out the next invoice number for a year, creating that year's sequence on first use.
        ///
        /// <para><b>A procedure, not a function — forced, not chosen.</b> The PostgreSQL original was a
        /// plpgsql function called from a <c>SELECT</c>. SQL Server scalar functions may not have side
        /// effects: no <c>CREATE SEQUENCE</c>, and not even <c>NEXT VALUE FOR</c>. Both halves of what
        /// this does are therefore only legal in a procedure, which is why
        /// <c>InvoiceNumberGenerator</c> calls it with an output parameter instead of selecting from
        /// it.</para>
        ///
        /// <para><b>The creation race is tolerated, not prevented</b>, the same as before. Two issuers
        /// of the first invoice of a year can both pass the catalog check and both try to create the
        /// sequence; the loser catches "there is already an object named …" and uses the winner's
        /// sequence, which is the outcome both wanted. Only that error is swallowed — the CATCH
        /// re-checks the catalog and rethrows if the sequence still does not exist, so a permission
        /// failure or a full filegroup cannot be mistaken for a lost race.</para>
        /// </summary>
        private static void CreateInvoiceNumbering(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                """
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
                """);

        // ══════════════════════════════════════════════════════════════════════════════════════
        // 3. Audit log partitioning
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Converts <c>platform.audit_logs</c> to a monthly range-partitioned table
        /// (ARCHITECTURE.md §2.6, AD-7).
        ///
        /// <para><b>No copy, no DEFAULT partition, no rename dance.</b> The PostgreSQL version had to
        /// rename the table aside, create a partitioned twin, copy every row and drop the original,
        /// because PostgreSQL cannot add a partition key to an existing table. SQL Server partitions a
        /// table by rebuilding its clustered index onto a partition scheme, which is one statement and
        /// moves the data in place. And there is no DEFAULT partition to create: a range function with
        /// N boundaries defines N+1 partitions spanning the whole domain, so a row for an unanticipated
        /// month lands in the trailing partition rather than being refused. The safety net the
        /// PostgreSQL version had to build explicitly — audit rows are written inside the transaction
        /// of the operation they describe, so a refused audit insert would fail that operation — is
        /// structural here.</para>
        ///
        /// <para><b>RANGE RIGHT</b>, so each boundary is the first instant of its month and the month's
        /// rows sit in the partition to its right. RANGE LEFT would put the boundary instant itself in
        /// the previous month's partition, and an event stamped exactly midnight on the first is not a
        /// hypothetical for a table written by machines.</para>
        ///
        /// <para><b>Every boundary is UTC, explicitly.</b> Deriving them from the server's time zone
        /// would let partition edges move with a deployment detail, and a row filed under the wrong
        /// month stays invisible until someone retires that partition and loses it. The column is
        /// <c>datetimeoffset</c> and comparisons on it are resolved in UTC, so a boundary written with
        /// a <c>+00:00</c> offset means the same instant whatever the server is set to.</para>
        /// </summary>
        private static void PartitionAuditLogs(MigrationBuilder migrationBuilder)
        {
            // One boundary to begin with: the first instant of the current UTC month. The lookahead is
            // added at the end of this migration by the maintenance procedure itself, so the migration
            // and every subsequent run create boundaries through exactly the same code path and cannot
            // disagree about where a month starts.
            migrationBuilder.Sql(
                """
                DECLARE @current_month datetimeoffset(7) = TODATETIMEOFFSET(
                    CONVERT(datetime2(7), DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1)), 0);

                DECLARE @sql nvarchar(max) =
                    N'CREATE PARTITION FUNCTION pf_audit_logs_monthly (datetimeoffset(7))
                      AS RANGE RIGHT FOR VALUES (''' + CONVERT(nvarchar(40), @current_month, 126) + N''')';

                EXEC sp_executesql @sql;
                """);

            // ALL TO ([PRIMARY]): one filegroup for every partition. Azure SQL Database supports no
            // other arrangement, and the point here is retirable month boundaries rather than storage
            // placement — spreading partitions across filegroups would buy nothing this platform uses
            // and would not survive the production target.
            migrationBuilder.Sql(
                """
                CREATE PARTITION SCHEME ps_audit_logs_monthly
                    AS PARTITION pf_audit_logs_monthly ALL TO ([PRIMARY]);
                """);

            // Rebuilding the clustered primary key onto the scheme is what actually partitions the
            // table. The key is already (id, created_at_utc) in the EF model — AuditLogConfiguration
            // explains why — which satisfies the rule that a unique index on a partitioned table must
            // contain the partitioning column.
            migrationBuilder.Sql(
                """
                ALTER TABLE platform.audit_logs DROP CONSTRAINT pk_audit_logs;

                ALTER TABLE platform.audit_logs
                    ADD CONSTRAINT pk_audit_logs PRIMARY KEY CLUSTERED (id, created_at_utc)
                    ON ps_audit_logs_monthly (created_at_utc);
                """);

            // The four secondary indexes move onto the scheme too, so they are partition-aligned: an
            // aligned index is retired with its partition in one metadata operation, an unaligned one
            // has to be rebuilt across the whole table instead. DROP_EXISTING keeps each index's name
            // and definition and only changes where it lives.
            //
            // created_at_utc joins the key of the three that do not already carry it. An index on a
            // partitioned table need not contain the partitioning column to be aligned, but including
            // it lets a query filtered by month seek within one partition rather than touching all of
            // them, which is what every audit-log listing does.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_logs_created_at_utc
                    ON platform.audit_logs (created_at_utc DESC)
                    WITH (DROP_EXISTING = ON) ON ps_audit_logs_monthly (created_at_utc);

                CREATE INDEX ix_audit_logs_entity_type_entity_id
                    ON platform.audit_logs (entity_type, entity_id, created_at_utc)
                    WITH (DROP_EXISTING = ON) ON ps_audit_logs_monthly (created_at_utc);

                CREATE INDEX ix_audit_logs_user_id
                    ON platform.audit_logs (user_id, created_at_utc)
                    WITH (DROP_EXISTING = ON) ON ps_audit_logs_monthly (created_at_utc);

                CREATE INDEX ix_audit_logs_action
                    ON platform.audit_logs (action, created_at_utc)
                    WITH (DROP_EXISTING = ON) ON ps_audit_logs_monthly (created_at_utc);
                """);
        }

        /// <summary>
        /// Adds monthly boundaries ahead of need. Idempotent, concurrency-safe, and the only code that
        /// creates a boundary — the migration above calls it too.
        ///
        /// <para><b>What "creating a partition" means here.</b> Splitting the trailing partition at a
        /// new boundary, not adding a table. The trailing partition already accepts every future row;
        /// the split is what makes a month separately retirable. So falling behind costs granularity,
        /// never a refused write — which is why <c>AuditLogPartitionMaintainer</c> can be, and remains,
        /// non-fatal.</para>
        ///
        /// <para><b><c>EXECUTE AS OWNER</c> is what lets the application call it.</b>
        /// <c>ALTER PARTITION FUNCTION</c> needs <c>ALTER ANY DATASPACE</c>, which <c>gymos_app</c> has
        /// no business holding; the owner (<c>gymos_migrator</c>, via <c>db_ddladmin</c>) does. This is
        /// the same problem the <c>SecureRuntimeDdlFunctions</c> migration solved on PostgreSQL with
        /// <c>SECURITY DEFINER</c>, and it is solved the same way and for the same reason: without it
        /// the app's every boot logs a permission error, and audit rows quietly stop being partitioned
        /// by month.</para>
        ///
        /// <para><b>The swallow is narrow.</b> The PostgreSQL version originally caught
        /// <c>WHEN others</c>, and that is precisely how a privilege failure came to be reported as
        /// routine housekeeping — a control that reports a permission error as "0 created" is worse
        /// than no control. This catches nothing by error number; it re-reads the catalog and swallows
        /// only if the boundary it wanted now exists, which is exactly the concurrent-split race and
        /// nothing else. Everything else is rethrown to the maintainer, whose Error path exists for
        /// it.</para>
        /// </summary>
        private static void CreatePartitionMaintenance(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
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
                """);

            migrationBuilder.Sql(
                """
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
                """);

            // Three months of headroom, matching what the PostgreSQL migration created: enough that
            // writes stay correctly partitioned even if the application is never restarted and no
            // maintenance runs for a quarter.
            migrationBuilder.Sql(
                """
                DECLARE @created int;
                DECLARE @current_month date = DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1);

                EXEC platform.ensure_audit_log_partitions
                    @from_month = @current_month, @months_ahead = 3, @created = @created OUTPUT;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Un-partitions by the reverse route: rebuild the clustered key and its aligned indexes
            // back onto PRIMARY, which moves the rows out of the scheme, and only then drop the scheme
            // and function — neither can be dropped while anything still uses it.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_audit_logs_created_at_utc
                    ON platform.audit_logs (created_at_utc DESC) WITH (DROP_EXISTING = ON) ON [PRIMARY];
                CREATE INDEX ix_audit_logs_entity_type_entity_id
                    ON platform.audit_logs (entity_type, entity_id) WITH (DROP_EXISTING = ON) ON [PRIMARY];
                CREATE INDEX ix_audit_logs_user_id
                    ON platform.audit_logs (user_id) WITH (DROP_EXISTING = ON) ON [PRIMARY];
                CREATE INDEX ix_audit_logs_action
                    ON platform.audit_logs (action) WITH (DROP_EXISTING = ON) ON [PRIMARY];
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE platform.audit_logs DROP CONSTRAINT pk_audit_logs;
                ALTER TABLE platform.audit_logs
                    ADD CONSTRAINT pk_audit_logs PRIMARY KEY CLUSTERED (id, created_at_utc) ON [PRIMARY];
                """);

            migrationBuilder.Sql(
                """
                DROP PROCEDURE IF EXISTS platform.ensure_audit_log_partitions;
                DROP FUNCTION IF EXISTS platform.audit_log_boundary_exists;
                DROP PARTITION SCHEME ps_audit_logs_monthly;
                DROP PARTITION FUNCTION pf_audit_logs_monthly;
                DROP PROCEDURE IF EXISTS platform.next_invoice_number;
                DROP FUNCTION IF EXISTS platform.fn_tenant_isolation;
                """);

            // The per-year sequences next_invoice_number created at runtime are dropped too — they are
            // this migration's objects even though no migration statement created them.
            migrationBuilder.Sql(
                """
                DECLARE @drop nvarchar(max) = N'';

                SELECT @drop = @drop + N'DROP SEQUENCE platform.' + QUOTENAME(s.name) + N';'
                  FROM sys.sequences s
                  JOIN sys.schemas sc ON sc.schema_id = s.schema_id
                 WHERE sc.name = N'platform' AND s.name LIKE N'invoice[_]number[_]seq[_]%';

                IF @drop <> N'' EXEC sp_executesql @drop;
                """);

            // The hand-written indexes and constraint, dropped explicitly. EF does not know they
            // exist, so nothing else will remove them.
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS ix_plan_prices_plan_cycle_currency ON platform.plan_prices;
                DROP INDEX IF EXISTS ux_daily_platform_stats_mrr_currency ON platform.daily_platform_stats_mrr;
                DROP INDEX IF EXISTS ux_idempotency_records_claim ON platform.idempotency_records;

                IF OBJECT_ID('platform.ck_invoices_single_currency', 'C') IS NOT NULL
                    ALTER TABLE platform.invoices DROP CONSTRAINT ck_invoices_single_currency;
                """);
        }
    }
}
