using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GymOS.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSqlServer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.EnsureSchema(
                name: "outbox");

            migrationBuilder.CreateTable(
                name: "audit_logs",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    user_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ip_address = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    user_agent = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    entity_type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    old_values = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    new_values = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_logs", x => new { x.id, x.created_at_utc });
                });

            migrationBuilder.CreateTable(
                name: "daily_platform_stats",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stat_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_gyms = table.Column<int>(type: "int", nullable: false),
                    active_gyms = table.Column<int>(type: "int", nullable: false),
                    suspended_gyms = table.Column<int>(type: "int", nullable: false),
                    provisioning_gyms = table.Column<int>(type: "int", nullable: false),
                    new_gyms = table.Column<int>(type: "int", nullable: false),
                    canceled_gyms = table.Column<int>(type: "int", nullable: false),
                    expired_gyms = table.Column<int>(type: "int", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_platform_stats", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "features",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    category = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    default_enabled = table.Column<bool>(type: "bit", nullable: false),
                    is_premium = table.Column<bool>(type: "bit", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_features", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gyms",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    slug = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    suspension_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    owner_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    owner_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    country_code = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: false),
                    time_zone_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    deleted_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_gyms", x => x.id);
                    table.CheckConstraint("ck_gyms_slug_lowercase", "slug = lower(slug)");
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    operation_key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    idempotency_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    claim_token = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    status = table.Column<int>(type: "int", nullable: false),
                    response_status = table.Column<int>(type: "int", nullable: false),
                    response_body = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_version = table.Column<int>(type: "int", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    aggregate_type = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    correlation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    causation_depth = table.Column<int>(type: "int", nullable: false),
                    payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    module = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    normalized_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    scope = table.Column<short>(type: "smallint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    is_system = table.Column<bool>(type: "bit", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscription_plans",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    trial_days = table.Column<int>(type: "int", nullable: false),
                    max_members = table.Column<int>(type: "int", nullable: true),
                    max_trainers = table.Column<int>(type: "int", nullable: true),
                    max_staff = table.Column<int>(type: "int", nullable: true),
                    storage_limit_mb = table.Column<long>(type: "bigint", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    is_public = table.Column<bool>(type: "bit", nullable: false),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_subscription_plans", x => x.id);
                    table.CheckConstraint("ck_subscription_plans_code_upper", "code = upper(code)");
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    password_hash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    first_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    last_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    suspension_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    failed_login_attempts = table.Column<int>(type: "int", nullable: false),
                    locked_out_until_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_login_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    password_changed_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    security_stamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "daily_platform_stats_mrr",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    stats_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    amount_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    amount_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "gym_branding",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    gym_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    logo_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    favicon_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    primary_colour = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    secondary_colour = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    custom_domain = table.Column<string>(type: "nvarchar(253)", maxLength: 253, nullable: true),
                    email_sender_name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    handler_name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    attempts = table.Column<int>(type: "int", nullable: false),
                    next_retry_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    last_error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => x.id);
                    table.ForeignKey(
                        name: "fk_deliveries_outbox_message_message_id",
                        column: x => x.message_id,
                        principalSchema: "outbox",
                        principalTable: "messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "platform",
                columns: table => new
                {
                    role_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    permission_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_role_permissions", x => new { x.role_id, x.permission_id });
                    table.ForeignKey(
                        name: "fk_role_permissions_permissions_permission_id",
                        column: x => x.permission_id,
                        principalSchema: "platform",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_role_permissions_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "platform",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    feature_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "plan_prices",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    billing_cycle = table.Column<short>(type: "smallint", nullable: false),
                    price_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    price_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "gym_feature_overrides",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    gym_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    feature_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    is_enabled = table.Column<bool>(type: "bit", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    set_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    set_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
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
                name: "refresh_tokens",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_by_ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: false),
                    revoked_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    revoked_by_ip = table.Column<string>(type: "nvarchar(45)", maxLength: 45, nullable: true),
                    revocation_reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    replaced_by_token_hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_refresh_tokens_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_activation_tokens",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    purpose = table.Column<int>(type: "int", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_activation_tokens", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_activation_tokens_user_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_user_activation_tokens_user_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    role_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_roles", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_roles_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "platform",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gym_subscriptions",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    gym_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    plan_price_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    billing_cycle = table.Column<short>(type: "smallint", nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    current_period_start_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    current_period_end_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    period_anchor_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    trial_ends_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    canceled_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    expired_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    cancel_at_period_end = table.Column<bool>(type: "bit", nullable: false),
                    auto_renew = table.Column<bool>(type: "bit", nullable: false),
                    cancellation_reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    pending_plan_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    pending_plan_price_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    pending_billing_cycle = table.Column<short>(type: "smallint", nullable: true),
                    pending_price_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: true),
                    pending_price_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: true),
                    pending_change_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    price_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    price_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
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

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    gym_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    invoice_number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<short>(type: "smallint", nullable: false),
                    tax_rate = table.Column<decimal>(type: "decimal(5,4)", nullable: false),
                    issued_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    due_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    paid_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    payment_reference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    subtotal_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    subtotal_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    tax_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    tax_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    total_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    total_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoices", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoices_gym_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "platform",
                        principalTable: "gym_subscriptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_invoices_gyms_gym_id",
                        column: x => x.gym_id,
                        principalSchema: "platform",
                        principalTable: "gyms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    quantity = table.Column<int>(type: "int", nullable: false),
                    amount_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    amount_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    unit_price_amount = table.Column<decimal>(type: "decimal(14,4)", nullable: false),
                    unit_price_currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "fk_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalSchema: "platform",
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_action",
                schema: "platform",
                table: "audit_logs",
                column: "action");

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_created_at_utc",
                schema: "platform",
                table: "audit_logs",
                column: "created_at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_entity_type_entity_id",
                schema: "platform",
                table: "audit_logs",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_logs_user_id",
                schema: "platform",
                table: "audit_logs",
                column: "user_id");

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

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_message_id_handler_name",
                schema: "outbox",
                table: "deliveries",
                columns: new[] { "message_id", "handler_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_status_next_retry_at_utc",
                schema: "outbox",
                table: "deliveries",
                columns: new[] { "status", "next_retry_at_utc" });

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

            migrationBuilder.CreateIndex(
                name: "ix_idempotency_records_expires_at",
                schema: "platform",
                table: "idempotency_records",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_invoice_lines_invoice_id",
                schema: "platform",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ix_invoices_gym_id_created_at_utc",
                schema: "platform",
                table: "invoices",
                columns: new[] { "gym_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_status_due_at_utc",
                schema: "platform",
                table: "invoices",
                columns: new[] { "status", "due_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_invoices_subscription_id",
                schema: "platform",
                table: "invoices",
                column: "subscription_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoices_invoice_number",
                schema: "platform",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_created_at_utc",
                schema: "outbox",
                table: "messages",
                column: "created_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_messages_event_id",
                schema: "outbox",
                table: "messages",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_event_name",
                schema: "outbox",
                table: "messages",
                column: "event_name");

            migrationBuilder.CreateIndex(
                name: "ix_permissions_key",
                schema: "platform",
                table: "permissions",
                column: "key",
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

            migrationBuilder.CreateIndex(
                name: "ix_plan_prices_plan_id_billing_cycle",
                schema: "platform",
                table: "plan_prices",
                columns: new[] { "plan_id", "billing_cycle" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_token_hash",
                schema: "platform",
                table: "refresh_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user_id_expires_at_utc",
                schema: "platform",
                table: "refresh_tokens",
                columns: new[] { "user_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_role_permissions_permission_id",
                schema: "platform",
                table: "role_permissions",
                column: "permission_id");

            migrationBuilder.CreateIndex(
                name: "ix_roles_normalized_name_tenant_id",
                schema: "platform",
                table: "roles",
                columns: new[] { "normalized_name", "tenant_id" },
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "ix_user_activation_tokens_created_by_user_id",
                schema: "platform",
                table: "user_activation_tokens",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_activation_tokens_expires_at_utc",
                schema: "platform",
                table: "user_activation_tokens",
                column: "expires_at_utc");

            migrationBuilder.CreateIndex(
                name: "ix_user_activation_tokens_token_hash",
                schema: "platform",
                table: "user_activation_tokens",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_activation_tokens_user_id_consumed_at_utc",
                schema: "platform",
                table: "user_activation_tokens",
                columns: new[] { "user_id", "consumed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role_id",
                schema: "platform",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_user_id_role_id_tenant_id",
                schema: "platform",
                table: "user_roles",
                columns: new[] { "user_id", "role_id", "tenant_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_normalized_email",
                schema: "platform",
                table: "users",
                column: "normalized_email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_logs",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "daily_platform_stats_mrr",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "outbox");

            migrationBuilder.DropTable(
                name: "gym_branding",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "gym_feature_overrides",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "idempotency_records",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "plan_features",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_activation_tokens",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "daily_platform_stats",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "outbox");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "features",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "users",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "gym_subscriptions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "gyms",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "plan_prices",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "subscription_plans",
                schema: "platform");
        }
    }
}
