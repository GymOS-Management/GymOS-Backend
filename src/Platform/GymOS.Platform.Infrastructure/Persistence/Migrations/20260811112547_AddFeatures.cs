using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "features",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    default_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    is_premium = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_features", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gym_feature_overrides",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    gym_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    set_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gym_feature_overrides", x => x.id);
                    table.ForeignKey(
                        name: "fk_gym_feature_overrides_features_feature_id",
                        column: x => x.feature_id,
                        principalSchema: "platform",
                        principalTable: "features",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_gym_feature_overrides_gyms_gym_id",
                        column: x => x.gym_id,
                        principalSchema: "platform",
                        principalTable: "gyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_gym_feature_overrides_user_set_by_user_id",
                        column: x => x.set_by_user_id,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    feature_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_features", x => x.id);
                    table.ForeignKey(
                        name: "fk_plan_features_features_feature_id",
                        column: x => x.feature_id,
                        principalSchema: "platform",
                        principalTable: "features",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_plan_features_subscription_plans_plan_id",
                        column: x => x.plan_id,
                        principalSchema: "platform",
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_features_category_name",
                schema: "platform",
                table: "features",
                columns: new[] { "category", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_features_key",
                schema: "platform",
                table: "features",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gym_feature_overrides_feature_id",
                schema: "platform",
                table: "gym_feature_overrides",
                column: "feature_id");

            migrationBuilder.CreateIndex(
                name: "ix_gym_feature_overrides_gym_id",
                schema: "platform",
                table: "gym_feature_overrides",
                column: "gym_id");

            migrationBuilder.CreateIndex(
                name: "ix_gym_feature_overrides_set_by_user_id",
                schema: "platform",
                table: "gym_feature_overrides",
                column: "set_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_gym_feature_overrides_gym_feature",
                schema: "platform",
                table: "gym_feature_overrides",
                columns: new[] { "gym_id", "feature_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_features_feature_id",
                schema: "platform",
                table: "plan_features",
                column: "feature_id");

            migrationBuilder.CreateIndex(
                name: "ux_plan_features_plan_feature",
                schema: "platform",
                table: "plan_features",
                columns: new[] { "plan_id", "feature_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gym_feature_overrides",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "plan_features",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "features",
                schema: "platform");
        }
    }
}
