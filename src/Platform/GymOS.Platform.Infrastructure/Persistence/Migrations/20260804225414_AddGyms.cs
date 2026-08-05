using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGyms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "gyms",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    suspension_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    owner_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    owner_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    time_zone_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gyms", x => x.id);
                    table.CheckConstraint("ck_gyms_slug_lowercase", "slug = lower(slug)");
                });

            migrationBuilder.CreateTable(
                name: "gym_branding",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    logo_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    favicon_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    primary_colour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    secondary_colour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    custom_domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    email_sender_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gym_branding", x => x.id);
                    table.ForeignKey(
                        name: "fk_gym_branding_gym_gym_id",
                        column: x => x.gym_id,
                        principalSchema: "platform",
                        principalTable: "gyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gym_branding_custom_domain",
                schema: "platform",
                table: "gym_branding",
                column: "custom_domain",
                unique: true,
                filter: "custom_domain IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_gym_branding_gym_id",
                schema: "platform",
                table: "gym_branding",
                column: "gym_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gyms_slug",
                schema: "platform",
                table: "gyms",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gyms_status_created_at_utc",
                schema: "platform",
                table: "gyms",
                columns: new[] { "status", "created_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gym_branding",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "gyms",
                schema: "platform");
        }
    }
}
