using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGymSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gym_subscriptions",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_price_id = table.Column<Guid>(type: "uuid", nullable: true),
                    billing_cycle = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    current_period_start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    current_period_end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trial_ends_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    canceled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancel_at_period_end = table.Column<bool>(type: "boolean", nullable: false),
                    auto_renew = table.Column<bool>(type: "boolean", nullable: false),
                    cancellation_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    price_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    price_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gym_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "fk_gym_subscriptions_gyms_gym_id",
                        column: x => x.gym_id,
                        principalSchema: "platform",
                        principalTable: "gyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gym_subscriptions_plan_price_plan_price_id",
                        column: x => x.plan_price_id,
                        principalSchema: "platform",
                        principalTable: "plan_prices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_gym_subscriptions_subscription_plan_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "platform",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_gym_id_created_at_utc",
                schema: "platform",
                table: "gym_subscriptions",
                columns: new[] { "gym_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_plan_id",
                schema: "platform",
                table: "gym_subscriptions",
                column: "plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_plan_price_id",
                schema: "platform",
                table: "gym_subscriptions",
                column: "plan_price_id");

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_status_current_period_end_utc",
                schema: "platform",
                table: "gym_subscriptions",
                columns: new[] { "status", "current_period_end_utc" });

            migrationBuilder.CreateIndex(
                name: "ux_gym_subscriptions_one_live_per_gym",
                schema: "platform",
                table: "gym_subscriptions",
                column: "gym_id",
                unique: true,
                filter: "status IN (1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gym_subscriptions",
                schema: "platform");
        }
    }
}
