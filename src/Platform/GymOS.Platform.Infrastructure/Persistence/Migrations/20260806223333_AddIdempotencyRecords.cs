using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    operation_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "jsonb", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "platform",
                table: "idempotency_records",
                column: "expires_at");

            // One claim per (tenant, operation, key) — AD-39 §1.4. Written as SQL because the
            // uniqueness must treat NULL tenant_id as a value (two NULL-tenant rows must
            // conflict), and PostgreSQL's NULLS-distinct semantics plus EF's inability to
            // index an expression make COALESCE-in-SQL the only honest spelling. Same
            // technique and same consequence as ix_plan_prices_plan_cycle_currency: the EF
            // model does not know this index; changes to the claim key happen here, by hand.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_idempotency_records_claim
                    ON platform.idempotency_records (
                        (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid)),
                        operation_key,
                        idempotency_key);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS platform.ux_idempotency_records_claim;");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "platform");
        }
    }
}
