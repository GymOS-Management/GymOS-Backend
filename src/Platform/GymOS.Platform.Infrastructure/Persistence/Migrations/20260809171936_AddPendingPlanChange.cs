using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingPlanChange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<short>(
                name: "pending_billing_cycle",
                schema: "platform",
                table: "gym_subscriptions",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_change_at_utc",
                schema: "platform",
                table: "gym_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_plan_id",
                schema: "platform",
                table: "gym_subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "pending_plan_price_id",
                schema: "platform",
                table: "gym_subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "pending_price_amount",
                schema: "platform",
                table: "gym_subscriptions",
                type: "numeric(14,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_price_currency",
                schema: "platform",
                table: "gym_subscriptions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "pending_billing_cycle",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_change_at_utc",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_plan_id",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_plan_price_id",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_price_amount",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "pending_price_currency",
                schema: "platform",
                table: "gym_subscriptions");
        }
    }
}
