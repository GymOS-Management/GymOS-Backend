using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionExpiredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expired_at_utc",
                schema: "platform",
                table: "gym_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_canceled_at_utc",
                schema: "platform",
                table: "gym_subscriptions",
                column: "canceled_at_utc",
                filter: "status = 4");

            migrationBuilder.CreateIndex(
                name: "ix_gym_subscriptions_expired_at_utc",
                schema: "platform",
                table: "gym_subscriptions",
                column: "expired_at_utc",
                filter: "status = 5");

            // Backfilled from the period end, and only for rows that already expired.
            //
            // That is the approximation this column exists to stop relying on, so it is worth being
            // exact about why it is right here and wrong in the compiler. For a NEW expiry the sweep
            // stamps the real moment, which can be days after the period end and is the whole point.
            // For a row that expired BEFORE this migration, no such record was ever kept — the period
            // end is the only evidence in the database, and the sweep applies expiries within fifteen
            // minutes of it in the ordinary case, so it is a close approximation rather than a guess.
            //
            // The alternative was leaving them NULL, which reads as honest and is worse: the new
            // predicate matches on `expired_at_utc`, so every pre-existing expiry would be invisible
            // to it forever, rather than merely imprecise. Nothing already written is affected either
            // way — stat rows are write-once and never recomputed.
            migrationBuilder.Sql(
                """
                UPDATE platform.gym_subscriptions
                SET expired_at_utc = current_period_end_utc
                WHERE status = 5 AND expired_at_utc IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_gym_subscriptions_canceled_at_utc",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropIndex(
                name: "ix_gym_subscriptions_expired_at_utc",
                schema: "platform",
                table: "gym_subscriptions");

            migrationBuilder.DropColumn(
                name: "expired_at_utc",
                schema: "platform",
                table: "gym_subscriptions");
        }
    }
}
