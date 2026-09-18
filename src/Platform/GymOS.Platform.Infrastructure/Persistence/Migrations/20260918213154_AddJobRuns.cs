using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "job_runs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    job_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    instance = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<byte>(type: "tinyint", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    error = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_job_runs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_job_runs_name_started",
                schema: "platform",
                table: "job_runs",
                columns: new[] { "job_name", "started_at_utc" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "job_runs",
                schema: "platform");
        }
    }
}
