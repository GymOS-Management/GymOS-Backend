namespace GymOS.Platform.Domain.Settings;

/// <summary>
/// The platform's declared settings (M7 spec §2) — the single source the settings surface enumerates,
/// exactly as <see cref="Identity.PlatformPermissions"/> is for permissions. Keys are stable API.
/// </summary>
public static class PlatformSettingDefinitions
{
    /// <summary>What a sensitive value reads as, and what a PUT sends to mean "unchanged".</summary>
    public const string Mask = "••••";

    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("general.platform_name", SettingCategory.General, SettingValueType.String, false,
            "The platform's display name, shown in the console header.", "GymOS", null),
        new("general.console_base_url", SettingCategory.General, SettingValueType.String, false,
            "Where the console lives; every activation link is built from it.", "http://localhost:3000", "Email:ConsoleBaseUrl"),

        new("email.enabled", SettingCategory.Email, SettingValueType.Bool, false,
            "Send administrator emails over SMTP. Off logs them instead.", "false", "Email:Enabled"),
        new("email.smtp_host", SettingCategory.Email, SettingValueType.String, false,
            "SMTP server host name.", null, "Email:Smtp:Host"),
        new("email.smtp_port", SettingCategory.Email, SettingValueType.Int, false,
            "SMTP server port.", "1025", "Email:Smtp:Port", Min: 1, Max: 65535),
        new("email.smtp_use_starttls", SettingCategory.Email, SettingValueType.Bool, false,
            "Negotiate STARTTLS after connecting.", "false", "Email:Smtp:UseStartTls"),
        new("email.smtp_username", SettingCategory.Email, SettingValueType.String, false,
            "SMTP user name; leave empty for an unauthenticated relay.", null, "Email:Smtp:Username"),
        new("email.smtp_password", SettingCategory.Email, SettingValueType.String, true,
            "SMTP password. Stored encrypted; never shown again.", null, "Email:Smtp:Password"),
        new("email.from_address", SettingCategory.Email, SettingValueType.String, false,
            "The From address on every platform email.", null, "Email:From:Address"),
        new("email.from_display_name", SettingCategory.Email, SettingValueType.String, false,
            "The From display name on every platform email.", null, "Email:From:DisplayName"),
        new("email.invitation_lifetime_hours", SettingCategory.Email, SettingValueType.Int, false,
            "How long an invitation link stays valid.", "72", "Email:InvitationLifetimeHours", Min: 1, Max: 336),
        new("email.password_reset_lifetime_hours", SettingCategory.Email, SettingValueType.Int, false,
            "How long a password-reset link stays valid.", "1", "Email:PasswordResetLifetimeHours", Min: 1, Max: 72),

        new("security.max_failed_attempts", SettingCategory.Security, SettingValueType.Int, false,
            "Wrong passwords before an account locks.", "5", "Security:LoginPolicy:MaxFailedAttempts", Min: 1, Max: 100),
        new("security.lockout_minutes", SettingCategory.Security, SettingValueType.Int, false,
            "How long a locked account stays locked.", "15", "Security:LoginPolicy:LockoutDuration", SettingDurationUnit.Minutes, 1, 1440),
        new("security.refresh_token_days", SettingCategory.Security, SettingValueType.Int, false,
            "How long a sign-in session may be refreshed.", "7", "Security:LoginPolicy:RefreshTokenLifetime", SettingDurationUnit.Days, 1, 90),
        new("security.minimum_password_length", SettingCategory.Security, SettingValueType.Int, false,
            "Shortest password an administrator may set.", "12", "Security:LoginPolicy:MinimumPasswordLength", Min: 8, Max: 128),
        new("security.access_token_minutes", SettingCategory.Security, SettingValueType.Int, false,
            "Access token lifetime.", "15", "Security:Jwt:AccessTokenLifetime", SettingDurationUnit.Minutes, 1, 120),
        new("security.sign_in_attempts_per_window", SettingCategory.Security, SettingValueType.Int, false,
            "Sign-in attempts one address may make per window.", "10", "Security:RateLimiting:SignInAttemptsPerWindow", Min: 1, Max: 1000),
        new("security.sign_in_window_seconds", SettingCategory.Security, SettingValueType.Int, false,
            "The sign-in rate-limit window.", "60", "Security:RateLimiting:SignInWindowSeconds", Min: 1, Max: 3600),
        new("security.api_requests_per_minute", SettingCategory.Security, SettingValueType.Int, false,
            "Requests one signed-in user may make per minute.", "600", "Security:RateLimiting:ApiRequestsPerMinute", Min: 1, Max: 100000),
        new("security.anonymous_requests_per_minute", SettingCategory.Security, SettingValueType.Int, false,
            "Requests one unauthenticated address may make per minute.", "60", "Security:RateLimiting:AnonymousRequestsPerMinute", Min: 1, Max: 100000),

        new("maintenance.enabled", SettingCategory.Maintenance, SettingValueType.Bool, false,
            "Refuse every request except health, sign-in and SuperAdmins.", "false", "Maintenance:Enabled"),
        new("maintenance.message", SettingCategory.Maintenance, SettingValueType.String, false,
            "What callers are told while maintenance is on.", "GymOS is undergoing maintenance. Please try again shortly.", "Maintenance:Message"),

        new("retention.job_runs_days", SettingCategory.Retention, SettingValueType.Int, false,
            "Job run history kept before the nightly sweep; the newest run per job is always kept.", "30", "Monitoring:JobRunRetentionDays", Min: 1, Max: 3650),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(definition => definition.Key, StringComparer.Ordinal);

    public static bool TryGet(string key, out SettingDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);

    public static IReadOnlyList<SettingDefinition> ForCategory(SettingCategory category) =>
        All.Where(definition => definition.Category == category).ToList();
}
