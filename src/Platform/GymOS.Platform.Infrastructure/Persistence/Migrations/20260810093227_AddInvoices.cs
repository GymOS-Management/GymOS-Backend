using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(5,4)", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    paid_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    payment_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    subtotal_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    tax_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    tax_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    total_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_gym_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "platform",
                        principalTable: "gym_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_gyms_gym_id",
                        column: x => x.gym_id,
                        principalSchema: "platform",
                        principalTable: "gyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    amount_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    amount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    unit_price_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    unit_price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "platform",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id",
                schema: "platform",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_gym_id_created_at_utc",
                schema: "platform",
                table: "invoices",
                columns: new[] { "gym_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_status_due_at_utc",
                schema: "platform",
                table: "invoices",
                columns: new[] { "status", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_subscription_id",
                schema: "platform",
                table: "invoices",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_invoice_number",
                schema: "platform",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            // ── Invoice numbering (frozen §2.3: sequential per year, DB sequence) ─────────
            // Raw SQL because EF has no notion of on-demand per-year sequences. Idempotent and
            // gap-tolerant: a rolled-back issuing transaction burns a number (flagged for counsel
            // in docs/BusinessRules/Invoices.md). Callers: InvoiceNumberGenerator today; the M3.4
            // renewal job through the same class later.
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

            // ── One currency per invoice (design deviation record: three Money columns replace
            // §2.3's single currency column because R4 forbids naked decimals; this CHECK holds
            // the stated property the single column would have given for free) ────────────────
            migrationBuilder.Sql(
                """
                ALTER TABLE platform.invoices
                    ADD CONSTRAINT ck_invoices_single_currency
                    CHECK (subtotal_currency = tax_currency AND tax_currency = total_currency);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The CHECK constraint drops with its table below. The per-year sequences are left in
            // place deliberately: they are cheap, and dropping them would reset numbering if the
            // migration were ever re-applied — reusing an invoice number is the one failure mode
            // this whole mechanism exists to prevent.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.next_invoice_number(integer);");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "platform");
        }
    }
}
