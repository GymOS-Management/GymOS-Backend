using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "data_protection_keys",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    friendly_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    xml = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_data_protection_keys", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "platform_settings",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    category = table.Column<byte>(type: "tinyint", nullable: false),
                    value_type = table.Column<byte>(type: "tinyint", nullable: false),
                    is_sensitive = table.Column<bool>(type: "bit", nullable: false),
                    value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    protected_value = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    updated_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_platform_settings", x => x.id);
                    table.CheckConstraint("ck_platform_settings_one_value", "([value] IS NULL AND [protected_value] IS NOT NULL) OR ([value] IS NOT NULL AND [protected_value] IS NULL)");
                });

            migrationBuilder.CreateIndex(
                name: "ux_platform_settings_key",
                schema: "platform",
                table: "platform_settings",
                column: "key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "data_protection_keys",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "platform_settings",
                schema: "platform");
        }
    }
}
