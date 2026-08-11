using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "daily_platform_stats",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stat_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_gyms = table.Column<int>(type: "integer", nullable: false),
                    active_gyms = table.Column<int>(type: "integer", nullable: false),
                    suspended_gyms = table.Column<int>(type: "integer", nullable: false),
                    provisioning_gyms = table.Column<int>(type: "integer", nullable: false),
                    new_gyms = table.Column<int>(type: "integer", nullable: false),
                    canceled_gyms = table.Column<int>(type: "integer", nullable: false),
                    expired_gyms = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_platform_stats", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "daily_platform_stats_mrr",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stats_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount_amount = table.Column<decimal>(type: "numeric(14,4)", nullable: false),
                    amount_currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_platform_stats_mrr", x => x.id);
                    table.ForeignKey(
                        name: "fk_daily_platform_stats_mrr_daily_platform_stats_stats_id",
                        column: x => x.stats_id,
                        principalSchema: "platform",
                        principalTable: "daily_platform_stats",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_daily_platform_stats_date",
                schema: "platform",
                table: "daily_platform_stats",
                column: "stat_date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_platform_stats_mrr_stats_id",
                schema: "platform",
                table: "daily_platform_stats_mrr",
                column: "stats_id");

            // EF cannot build an index across a complex property's member, so the (stats, currency)
            // uniqueness is expressed here — the AddSubscriptionPlans precedent for prices.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_daily_platform_stats_mrr_currency
                ON platform.daily_platform_stats_mrr (stats_id, amount_currency);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS platform.ux_daily_platform_stats_mrr_currency;");

            migrationBuilder.DropTable(
                name: "daily_platform_stats_mrr",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "daily_platform_stats",
                schema: "platform");
        }
    }
}
