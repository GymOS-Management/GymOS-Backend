using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "subscription_plans",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    trial_days = table.Column<int>(type: "integer", nullable: false),
                    max_members = table.Column<int>(type: "integer", nullable: true),
                    max_trainers = table.Column<int>(type: "integer", nullable: true),
                    max_staff = table.Column<int>(type: "integer", nullable: true),
                    storage_limit_mb = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    is_public = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plans", x => x.id);
                    table.CheckConstraint("ck_subscription_plans_code_upper", "code = upper(code)");
                });

            migrationBuilder.CreateTable(
                name: "plan_prices",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_cycle = table.Column<short>(type: "smallint", nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_prices", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_prices_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "platform",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_plan_prices_plan_id_billing_cycle",
                schema: "platform",
                table: "plan_prices",
                columns: new[] { "plan_id", "billing_cycle" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plans_code",
                schema: "platform",
                table: "subscription_plans",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plans_is_active_sort_order",
                schema: "platform",
                table: "subscription_plans",
                columns: new[] { "is_active", "sort_order" });

            // One price per (plan, billing cycle, currency) — ARCHITECTURE.md §2.3.
            //
            // Written as SQL rather than declared in SubscriptionPlanConfiguration because `currency`
            // lives inside the Money complex property (AD-42), and EF cannot build an index across a
            // complex type's member: it reports "the property 'Price_Currency' cannot be added ... no
            // corresponding CLR property or field". The rule is also enforced in the aggregate, so a
            // caller gets a typed conflict instead of a database error; this index is what holds under a
            // race between two operators setting the same price.
            //
            // Consequence worth knowing: the EF model does not know about this index, so it will not
            // appear in the model snapshot and `has-pending-model-changes` stays clean either way. Any
            // future change to the price key must be made here, by hand, in a new migration.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ix_plan_prices_plan_cycle_currency
                    ON platform.plan_prices (plan_id, billing_cycle, price_currency);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropped explicitly: EF does not know this index exists, so DropTable is not guaranteed to
            // be the thing that removes it in every provider version.
            migrationBuilder.Sql("DROP INDEX IF EXISTS platform.ix_plan_prices_plan_cycle_currency;");

            migrationBuilder.DropTable(
                name: "plan_prices",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "subscription_plans",
                schema: "platform");
        }
    }
}
