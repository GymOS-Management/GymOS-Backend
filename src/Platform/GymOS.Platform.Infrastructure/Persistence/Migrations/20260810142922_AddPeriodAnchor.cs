using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPeriodAnchor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "period_anchor_utc",
                schema: "platform",
                table: "gym_subscriptions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            // ── Backfill ──────────────────────────────────────────────────────────────────
            // Existing subscriptions anchor at their current period start — the best available
            // record of the day-of-month the gym signed up on. Rows assigned on the 29th-31st
            // whose period already clamped keep the clamped day; only subscriptions renewed
            // BEFORE this migration can carry that history, and none have been (the renewal job
            // ships in the same increment as this column).
            migrationBuilder.Sql(
                """
                UPDATE platform.gym_subscriptions
                    SET period_anchor_utc = current_period_start_utc;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "period_anchor_utc",
                schema: "platform",
                table: "gym_subscriptions");
        }
    }
}
