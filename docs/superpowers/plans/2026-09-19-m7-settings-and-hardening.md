# M7 — Settings & Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A code-declared settings catalog over `platform.platform_settings` with typed `ISettings<T>` consumption, sensitive values under Data Protection, `GET/PUT /settings`, an SMTP connection test, a maintenance-mode switch and middleware, a settings-tunable global rate limiter, and a streamed, audited audit-log CSV export.

**Architecture:** Definitions live in code (`PlatformSettingDefinitions`); rows hold only values someone set. `SettingsProvider` loads all rows once per TTL into a per-instance cache, decrypts sensitive rows, and overlays them onto `IConfiguration` by each definition's configuration path; `ISettings<T>` binds that merged view into the **existing** options classes and runs their **existing** `IValidateOptions<T>` chains. Writes validate the would-be options object before persisting. Consumers change type only (`IOptions<T>` → `ISettings<T>`).

**Tech Stack:** .NET 10 (SDK 10.0.400), ASP.NET Core minimal APIs, EF Core + SQL Server (`UseSnakeCaseNamingConvention`), ASP.NET Data Protection (EF Core key store, optional Azure Key Vault protector), MailKit, `System.Threading.RateLimiting`, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-19-m7-settings-and-hardening-design.md`

## Global Constraints

- Build clean with warnings as errors: `dotnet build GymOS.Backend.slnx -c Release`. `dotnet format GymOS.Backend.slnx --verify-no-changes` before every commit.
- `DateTimeOffset.UtcNow` / `DateTime.UtcNow` are **banned** (BannedSymbols.txt, RS0030 as error). Use `IClock`, `SystemClock`, `FakeClock`, or fixed constants.
- Every new command/query carries `[RequiresPermission(...)]`. Permission keys are exactly `settings.read`, `settings.manage`, `audit.export`.
- Tables: `platform.platform_settings` and `platform.data_protection_keys`; snake_case columns derived by the naming convention (no `HasColumnName` by hand). Migration `AddPlatformSettings` and its declarative twin in `database/GymOS.Database/Tables/platform/` land in the **same commit** (DATABASE.md §3).
- Keys are `{category}.{name}`; value types are `String | Int | Bool | Json`; durations are `Int` with the unit in the key and a `DurationUnit` on the definition, converted to a TimeSpan string when overlaying a TimeSpan option property.
- Mask is the literal string `••••` (four U+2022). PUT: mask on a sensitive key = unchanged; `""` or `null` = delete the row; anything else = replace.
- Data Protection purpose: `GymOS.PlatformSettings.v1`, sub-purpose = the setting key. Key Vault protector only when `DataProtection:KeyVaultKeyId` is set.
- `PUT /settings` refuses category `Maintenance` (`settings.use_maintenance_endpoint`). `POST /settings/maintenance-mode` requires `X-Confirm-Action: maintenance`.
- Maintenance middleware: after `UseAuthentication()`, before `UseAuthorization()`; always passes `/health/live`, `/health/ready`, `/version`, `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh`; passes when the caller holds `settings.manage`; otherwise 503 problem details, `Retry-After: 300`.
- Rate limits: authenticated sliding window per user id, default 600/min; anonymous per IP, default 60/min; sign-in fixed window default 10 per 60 s; `/health/*` and `/version` unlimited; 429 with `Retry-After`.
- Export: `from` and `to` required, inclusive, ≤ 366 days; CSV columns in order `id,created_at_utc,user_email,user_id,ip_address,action,entity_type,entity_id,correlation_id`; every field quoted, quotes doubled, CRLF; cells starting with `=`, `+`, `-`, `@`, tab, CR get a leading `'`.
- Job/role/claim facts: the JWT carries **no role claim**; "SuperAdmin passes" is implemented as `IPermissionEvaluator.HasPermissionAsync("settings.manage")`, which only SuperAdmin holds.
- New packages: `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (covered by the approved `Microsoft.AspNetCore.` prefix), `Azure.Extensions.AspNetCore.DataProtection.Keys` and `Azure.Identity` (covered by the approved `Azure.` prefix) — versions in `Directory.Packages.props`, rows in `docs/Engineering/DEPENDENCY_POLICY.md`, same commit as first use.
- Commits go straight to `development`; each task ends in a commit. Push only in the last task, after the controller's final review. Do not edit `docs/STATUS.md` before the last task.
- Commit trailers, verbatim: `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE`.
- Every command runs from `C:\Work\Personal-Projects\GymOS\GymOS-Backend`; Docker Desktop must be running for `GymOS.IntegrationTests`.

---

## File map

**Platform.Domain** (`src/Platform/GymOS.Platform.Domain/Settings/`)
- `SettingCategory.cs`, `SettingValueType.cs`, `SettingDurationUnit.cs` — enums.
- `SettingDefinition.cs` — the record + `SettingValueRules` (type/range check).
- `PlatformSettingDefinitions.cs` — the catalog.
- `PlatformSetting.cs` — the aggregate.
- Modify `Identity/PlatformPermissions.cs`, `Identity/SystemRoles.cs`.

**Platform.Application** (`src/Platform/GymOS.Platform.Application/Settings/`)
- `Abstractions/ISettingsRepository.cs`, `Abstractions/ISettingsProvider.cs`, `Abstractions/ISettings.cs`, `Abstractions/ISettingProtector.cs`, `Abstractions/ISettingsValidator.cs`, `Abstractions/ISmtpConnectionTester.cs`, `Abstractions/SettingsContracts.cs` (DTOs).
- `MaintenanceOptions.cs`, `RateLimitOptions.cs` — new options classes.
- `SettingErrors.cs`, `SettingsWriter.cs` (shared write pipeline), `SettingsQueries.cs` (GET), `SettingsCommands.cs` (PUT, maintenance, test-email).
- `Auditing/AuditCsv.cs`, `Auditing/ExportAuditLogs.cs`; modify `Auditing/IAuditLogQueries.cs`.

**Platform.Infrastructure** (`src/Platform/GymOS.Platform.Infrastructure/Settings/`)
- `SettingsRepository.cs`, `DataProtectionSettingProtector.cs`, `SettingsProvider.cs`, `SettingsBinder.cs` (`ISettings<T>`), `OptionsSettingsValidator.cs`, `MailKitSmtpConnectionTester.cs`, `SwitchingEmailSender.cs`, `SettingsProviderOptions.cs`.
- `Persistence/Configurations/PlatformSettingConfiguration.cs`, `Persistence/Configurations/DataProtectionKeyConfiguration.cs`; modify `Persistence/AppDbContext.cs`, `Persistence/Auditing/PlatformAuditedEntities.cs`, `Persistence/Queries/AuditLogQueries.cs`.
- `DependencyInjection/PlatformSettingsServiceCollectionExtensions.cs`; modify `PlatformIdentityServiceCollectionExtensions.cs` (email sender registration), `PlatformMonitoringServiceCollectionExtensions.cs` (nothing), consumers listed in Task 5.
- Migration `AddPlatformSettings` (generated).

**Host** (`src/Host/GymOS.WebApi/`)
- `Endpoints/SettingsEndpoints.cs`; modify `Endpoints/AuditLogEndpoints.cs`, `Extensions/RateLimitingExtensions.cs`, `Middleware/MaintenanceModeMiddleware.cs` (new), `Program.cs`.

**Database project**: `database/GymOS.Database/Tables/platform/platform_settings.sql`, `data_protection_keys.sql`.

**Tests**: Domain.UnitTests `Settings/PlatformSettingDefinitionsTests.cs`, `Settings/PlatformSettingTests.cs`; Application.UnitTests `SettingsCommandTests.cs`, `AuditCsvTests.cs`; Infrastructure.UnitTests `ConcurrencyModelTests.cs` (exemption), `SettingsCatalogBindingTests.cs`; IntegrationTests `SettingsProviderTests.cs`, `SettingsApiTests.cs`, `MaintenanceModeTests.cs`, `RateLimitingTests.cs`, `AuditExportTests.cs`, `FakeSmtpServer.cs`, `ApiEndpointTests.cs` (tripwire), `RetentionJobTests.cs`/`JobRunMaintainerTests.cs` (constructor change), OpenAPI snapshot `contracts/openapi-v1.json`.

**Docs**: `docs/BusinessRules/Settings.md`, `docs/BusinessRules/README.md`, `docs/RELEASE-NOTES.md`, `docs/Infrastructure/AZURE.md`, `docs/Engineering/DEPENDENCY_POLICY.md`, `docs/STATUS.md`.

---

### Task 1: The settings catalog — enums, `SettingDefinition`, `PlatformSettingDefinitions`

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Settings/SettingCategory.cs`, `SettingValueType.cs`, `SettingDurationUnit.cs`, `SettingDefinition.cs`, `PlatformSettingDefinitions.cs`
- Test: `tests/GymOS.Platform.Domain.UnitTests/Settings/PlatformSettingDefinitionsTests.cs`

**Interfaces:**
- Produces (namespace `GymOS.Platform.Domain.Settings`):
  ```csharp
  public enum SettingCategory { General = 1, Branding = 2, Email = 3, Storage = 4, AI = 5, Payment = 6, Security = 7, Maintenance = 8, Retention = 9 }
  public enum SettingValueType { String = 1, Int = 2, Bool = 3, Json = 4 }
  public enum SettingDurationUnit { None = 0, Seconds = 1, Minutes = 2, Hours = 3, Days = 4 }
  public sealed record SettingDefinition(string Key, SettingCategory Category, SettingValueType ValueType, bool IsSensitive,
      string Description, string? Default, string? ConfigurationPath, SettingDurationUnit Unit = SettingDurationUnit.None, int? Min = null, int? Max = null)
  {
      public string? ValidateValue(string value);            // null when valid, else the rule broken ("must be an integer", "must be between 1 and 65535", "must be true or false", "must be valid JSON")
      public string? ToConfigurationValue(string value);     // Int with a Unit → TimeSpan "c" string; else the value itself
      public string? FromConfigurationValue(string? raw);    // inverse: a TimeSpan string → whole units as text
  }
  public static class PlatformSettingDefinitions
  {
      public const string Mask = "••••";
      public static IReadOnlyList<SettingDefinition> All { get; }
      public static bool TryGet(string key, out SettingDefinition definition);
      public static IReadOnlyList<SettingDefinition> ForCategory(SettingCategory category);
  }
  ```
  Keys, defaults and paths are the spec §2 table verbatim, with **one correction the controller ruled**: `security.sign_in_window_seconds` overlays the **new** path `Security:RateLimiting:SignInWindowSeconds` (an `int`), not the TimeSpan `SignInWindow`; Task 10 makes the limiter read the new property.

- [ ] **Step 1: Write the failing tests**

```csharp
using GymOS.Platform.Domain.Settings;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests.Settings;

public sealed class PlatformSettingDefinitionsTests
{
    [Fact]
    public void EveryKeyIsCategoryDotNameAndTheCategoryMatchesThePrefix()
    {
        foreach (SettingDefinition definition in PlatformSettingDefinitions.All)
        {
            string[] parts = definition.Key.Split('.');
            Assert.Equal(2, parts.Length);
            Assert.Equal(definition.Category.ToString().ToLowerInvariant(), parts[0]);
            Assert.Matches("^[a-z][a-z0-9_]*$", parts[1]);
        }
    }

    [Fact]
    public void KeysAreUnique() =>
        Assert.Equal(PlatformSettingDefinitions.All.Count, PlatformSettingDefinitions.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());

    [Fact]
    public void EveryDefaultIsValidForItsOwnDefinition()
    {
        foreach (SettingDefinition definition in PlatformSettingDefinitions.All.Where(d => d.Default is not null))
        {
            Assert.Null(definition.ValidateValue(definition.Default!));
        }
    }

    [Fact]
    public void TheCatalogHoldsTheSpecsTwentyFiveKeys()
    {
        Assert.Equal(25, PlatformSettingDefinitions.All.Count);
        Assert.True(PlatformSettingDefinitions.TryGet("email.smtp_password", out SettingDefinition password));
        Assert.True(password.IsSensitive);
        Assert.Equal("Email:Smtp:Password", password.ConfigurationPath);
    }

    [Theory]
    [InlineData("email.smtp_port", "0", "must be between 1 and 65535")]
    [InlineData("email.smtp_port", "abc", "must be an integer")]
    [InlineData("email.enabled", "yes", "must be true or false")]
    [InlineData("security.max_failed_attempts", "-1", "must be between 1 and 100")]
    public void ValuesAreCheckedAgainstTypeAndRange(string key, string value, string expectedRule)
    {
        Assert.True(PlatformSettingDefinitions.TryGet(key, out SettingDefinition definition));
        Assert.Equal(expectedRule, definition.ValidateValue(value));
    }

    [Fact]
    public void ADurationOverlaysAsATimeSpanString()
    {
        Assert.True(PlatformSettingDefinitions.TryGet("security.lockout_minutes", out SettingDefinition lockout));
        Assert.Equal("00:15:00", lockout.ToConfigurationValue("15"));
        Assert.Equal("15", lockout.FromConfigurationValue("00:15:00"));
        Assert.True(PlatformSettingDefinitions.TryGet("security.refresh_token_days", out SettingDefinition refresh));
        Assert.Equal("7.00:00:00", refresh.ToConfigurationValue("7"));
        Assert.Equal("7", refresh.FromConfigurationValue("7.00:00:00"));
    }

    [Fact]
    public void ForCategoryReturnsOnlyThatCategory() =>
        Assert.All(PlatformSettingDefinitions.ForCategory(SettingCategory.Email), d => Assert.Equal(SettingCategory.Email, d.Category));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~PlatformSettingDefinitionsTests"`
Expected: build error — namespace missing.

- [ ] **Step 3: Implement**

`SettingCategory.cs`, `SettingValueType.cs`, `SettingDurationUnit.cs` — the three enums above, each with a one-line summary (`Retention` documented as "additive to ARCHITECTURE.md §2.7's list; see the M7 spec §1").

`SettingDefinition.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace GymOS.Platform.Domain.Settings;

/// <summary>
/// One declared setting (M7 spec §2). Definitions are code; rows are values. A value without a
/// definition cannot be written, and the definition is what a console renders — type, range,
/// description — whether or not a row exists.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingCategory Category,
    SettingValueType ValueType,
    bool IsSensitive,
    string Description,
    string? Default,
    string? ConfigurationPath,
    SettingDurationUnit Unit = SettingDurationUnit.None,
    int? Min = null,
    int? Max = null)
{
    /// <summary>The rule a value breaks, or null when it is valid for this definition.</summary>
    public string? ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (ValueType)
        {
            case SettingValueType.Int:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    return "must be an integer";
                }

                if ((Min is int min && number < min) || (Max is int max && number > max))
                {
                    return $"must be between {Min ?? int.MinValue} and {Max ?? int.MaxValue}";
                }

                return null;

            case SettingValueType.Bool:
                return bool.TryParse(value, out _) ? null : "must be true or false";

            case SettingValueType.Json:
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(value);
                    return null;
                }
                catch (JsonException)
                {
                    return "must be valid JSON";
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// The text to place at <see cref="ConfigurationPath"/>: a duration setting stored as a whole
    /// number becomes the TimeSpan string the options property binds, so "15" for a minutes key
    /// overlays as "00:15:00" rather than binding as fifteen days.
    /// </summary>
    public string? ToConfigurationValue(string value)
    {
        if (Unit == SettingDurationUnit.None || ValueType != SettingValueType.Int)
        {
            return value;
        }

        int amount = int.Parse(value, CultureInfo.InvariantCulture);
        TimeSpan span = Unit switch
        {
            SettingDurationUnit.Seconds => TimeSpan.FromSeconds(amount),
            SettingDurationUnit.Minutes => TimeSpan.FromMinutes(amount),
            SettingDurationUnit.Hours => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };

        return span.ToString("c", CultureInfo.InvariantCulture);
    }

    /// <summary>The inverse of <see cref="ToConfigurationValue"/>, for showing a configuration-sourced duration in the key's unit.</summary>
    public string? FromConfigurationValue(string? raw)
    {
        if (raw is null || Unit == SettingDurationUnit.None || ValueType != SettingValueType.Int)
        {
            return raw;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan span))
        {
            return raw;
        }

        double amount = Unit switch
        {
            SettingDurationUnit.Seconds => span.TotalSeconds,
            SettingDurationUnit.Minutes => span.TotalMinutes,
            SettingDurationUnit.Hours => span.TotalHours,
            _ => span.TotalDays,
        };

        return ((long)Math.Round(amount)).ToString(CultureInfo.InvariantCulture);
    }
}
```

`PlatformSettingDefinitions.cs` — the catalog. Every row of the spec §2 table, in this shape (write **all 25**; the first rows shown, the rest follow the table exactly):

```csharp
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
```

Note `security.api_requests_per_minute` / `anonymous_requests_per_minute` overlay **new** paths under `Security:RateLimiting:` — Task 4 defines `RateLimitOptions` bound to that section with matching property names (`ApiRequestsPerMinute`, `AnonymousRequestsPerMinute`, `SignInAttemptsPerWindow`, `SignInWindowSeconds`). `maintenance.*` overlay `Maintenance:` for the new `MaintenanceOptions`.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~PlatformSettingDefinitionsTests"`
Expected: 7 PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Platform/GymOS.Platform.Domain/Settings tests/GymOS.Platform.Domain.UnitTests/Settings
git commit -m "settings: the declared catalog — 25 keys, five categories, typed and ranged

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 2: `PlatformSetting` aggregate, both tables, migration, transcription, packages

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Settings/PlatformSetting.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/PlatformSettingConfiguration.cs`, `DataProtectionKeyConfiguration.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/AppDbContext.cs`, `Persistence/Auditing/PlatformAuditedEntities.cs`, `src/Platform/GymOS.Platform.Infrastructure/GymOS.Platform.Infrastructure.csproj`, `Directory.Packages.props`, `docs/Engineering/DEPENDENCY_POLICY.md`
- Create (generated): migration `AddPlatformSettings` + Designer + snapshot; `database/GymOS.Database/Tables/platform/platform_settings.sql`, `data_protection_keys.sql`
- Modify: `tests/GymOS.Platform.Infrastructure.UnitTests/ConcurrencyModelTests.cs`
- Test: `tests/GymOS.Platform.Domain.UnitTests/Settings/PlatformSettingTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed class PlatformSetting : AggregateRoot, IAuditable
  {
      public const int MaxKeyLength = 100, MaxDescriptionLength = 500;
      public static PlatformSetting Create(Guid id, SettingDefinition definition, string? plainValue, string? protectedValue, Guid? updatedBy);
      public void Set(string? plainValue, string? protectedValue, Guid? updatedBy);   // throws DomainException if the populated column does not match IsSensitive, or both/neither are set
      public string Key { get; } public SettingCategory Category { get; } public SettingValueType ValueType { get; } public bool IsSensitive { get; }
      public string? Value { get; } public string? ProtectedValue { get; } public string Description { get; } public Guid? UpdatedBy { get; }
      public DateTimeOffset CreatedAtUtc { get; set; } public DateTimeOffset UpdatedAtUtc { get; set; }   // IAuditable, set by the interceptor
  }
  ```
  `AppDbContext : DbContext, IDataProtectionKeyContext` with `DbSet<DataProtectionKey> DataProtectionKeys`.

- [ ] **Step 1: Write the failing domain tests**

```csharp
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests.Settings;

public sealed class PlatformSettingTests
{
    private static SettingDefinition Plain => PlatformSettingDefinitions.All.Single(d => d.Key == "email.smtp_host");
    private static SettingDefinition Secret => PlatformSettingDefinitions.All.Single(d => d.Key == "email.smtp_password");

    [Fact]
    public void APlainSettingHoldsItsValueInTheClear()
    {
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Plain, "smtp.example.test", null, null);
        Assert.Equal("smtp.example.test", setting.Value);
        Assert.Null(setting.ProtectedValue);
        Assert.False(setting.IsSensitive);
    }

    [Fact]
    public void ASensitiveSettingHoldsOnlyCiphertext()
    {
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Secret, null, "CfDJ8...", null);
        Assert.Null(setting.Value);
        Assert.Equal("CfDJ8...", setting.ProtectedValue);
        Assert.True(setting.IsSensitive);
    }

    [Fact]
    public void APlainValueOnASensitiveDefinitionIsRefused() =>
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Secret, "plaintext", null, null));

    [Fact]
    public void CiphertextOnAPlainDefinitionIsRefused() =>
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, null, "CfDJ8...", null));

    [Fact]
    public void ExactlyOneColumnMustBePopulated()
    {
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, null, null, null));
        Assert.Throws<DomainException>(() => PlatformSetting.Create(Guid.NewGuid(), Plain, "a", "b", null));
    }

    [Fact]
    public void SetReplacesTheValueAndRecordsWho()
    {
        Guid editor = Guid.NewGuid();
        PlatformSetting setting = PlatformSetting.Create(Guid.NewGuid(), Plain, "old", null, null);
        setting.Set("new", null, editor);
        Assert.Equal("new", setting.Value);
        Assert.Equal(editor, setting.UpdatedBy);
    }
}
```

Check the `DomainException` namespace first: `grep -rn "class DomainException" src --include=*.cs`. Adjust the `using`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~PlatformSettingTests"`
Expected: build error.

- [ ] **Step 3: Write the aggregate**

```csharp
using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Settings;

/// <summary>
/// One stored value for one declared setting (M7 spec §3). Rows exist only for values someone has
/// set; the catalog supplies everything else. <see cref="Value"/> and <see cref="ProtectedValue"/> are
/// separate columns so the audit interceptor can redact the second by name and record the first in
/// plain — "who changed the SMTP host from what to what" stays answerable while the password never is.
/// An aggregate with a row version because two administrators can edit the same key.
/// </summary>
public sealed class PlatformSetting : AggregateRoot, IAuditable
{
    public const int MaxKeyLength = 100;
    public const int MaxDescriptionLength = 500;

    private PlatformSetting(Guid id, SettingDefinition definition)
        : base(id)
    {
        Key = definition.Key;
        Category = definition.Category;
        ValueType = definition.ValueType;
        IsSensitive = definition.IsSensitive;
        Description = definition.Description.Length <= MaxDescriptionLength
            ? definition.Description
            : definition.Description[..MaxDescriptionLength];
    }

    private PlatformSetting()
    {
    }

    public string Key { get; private set; } = string.Empty;

    public SettingCategory Category { get; private set; }

    public SettingValueType ValueType { get; private set; }

    public bool IsSensitive { get; private set; }

    /// <summary>The value in the clear. Always null for a sensitive key.</summary>
    public string? Value { get; private set; }

    /// <summary>Data Protection ciphertext. Always null for a non-sensitive key.</summary>
    public string? ProtectedValue { get; private set; }

    public string Description { get; private set; } = string.Empty;

    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static PlatformSetting Create(
        Guid id, SettingDefinition definition, string? plainValue, string? protectedValue, Guid? updatedBy)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var setting = new PlatformSetting(id, definition);
        setting.Set(plainValue, protectedValue, updatedBy);
        return setting;
    }

    public void Set(string? plainValue, string? protectedValue, Guid? updatedBy)
    {
        if ((plainValue is null) == (protectedValue is null))
        {
            throw new DomainException($"Setting '{Key}' must hold exactly one of a plain value or a protected value.");
        }

        if (IsSensitive && plainValue is not null)
        {
            throw new DomainException($"Setting '{Key}' is sensitive and cannot hold a plain value.");
        }

        if (!IsSensitive && protectedValue is not null)
        {
            throw new DomainException($"Setting '{Key}' is not sensitive and cannot hold a protected value.");
        }

        Value = plainValue;
        ProtectedValue = protectedValue;
        UpdatedBy = updatedBy;
    }
}
```

Confirm `DomainException` exists with a `(string message)` constructor and how other aggregates raise it (`grep -rn "throw new DomainException" src/Platform/GymOS.Platform.Domain --include=*.cs | head -3`); match.

- [ ] **Step 4: Run the domain tests**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~PlatformSettingTests"`
Expected: 6 PASS.

- [ ] **Step 5: Add the packages**

`Directory.Packages.props`: add, beside the other `10.0.11` ASP.NET lines, `<PackageVersion Include="Microsoft.AspNetCore.DataProtection.EntityFrameworkCore" Version="10.0.11" />`, and in a new comment-led group `<PackageVersion Include="Azure.Extensions.AspNetCore.DataProtection.Keys" Version="1.6.0" />` and `<PackageVersion Include="Azure.Identity" Version="1.17.1" />` — then run `dotnet restore` and, if NuGet reports a version does not exist, use the latest stable it lists (`dotnet package search <id> --exact-match`) and record the version you used in your report.

`GymOS.Platform.Infrastructure.csproj`: `<PackageReference Include="Microsoft.AspNetCore.DataProtection.EntityFrameworkCore" />` (the key store is an Infrastructure concern: it needs `AppDbContext`). `GymOS.WebApi.csproj`: `<PackageReference Include="Azure.Extensions.AspNetCore.DataProtection.Keys" />` and `<PackageReference Include="Azure.Identity" />` (the Key Vault protector is wired in the host).

`docs/Engineering/DEPENDENCY_POLICY.md`, "Approved — in use now" table, three rows in the existing style:

```
| `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` | MIT | Infrastructure | **Approved 2026-09-19 with M7.** Persists the Data Protection key ring in `platform.data_protection_keys` so a replica that scales from zero can still decrypt yesterday's sensitive setting (AD-9). Already inside the approved `Microsoft.AspNetCore.` family |
| `Azure.Extensions.AspNetCore.DataProtection.Keys` | MIT | **Host only** | **Approved 2026-09-19 with M7.** Wraps the key ring with an Azure Key Vault key when `DataProtection:KeyVaultKeyId` is set — AD-9's "keys in Key Vault in production". Inert until the vault exists (AZURE.md §7) |
| `Azure.Identity` | MIT | **Host only** | **Approved 2026-09-19 with M7.** `DefaultAzureCredential` for the Key Vault protector — the Container App's managed identity in Azure, the developer's `az login` locally. No secret is stored |
```

`ForbiddenPackagesTests` needs no change (both prefixes are approved); run it anyway.

- [ ] **Step 6: Configurations, DbContext, audited-entities registration, exemption**

`PlatformSettingConfiguration.cs`:

```csharp
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class PlatformSettingConfiguration : IEntityTypeConfiguration<PlatformSetting>
{
    public void Configure(EntityTypeBuilder<PlatformSetting> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("platform_settings", "platform", table =>
            // Exactly one of the two value columns is populated; the aggregate enforces it in code and
            // the database holds it under a bug.
            table.HasCheckConstraint(
                "ck_platform_settings_one_value",
                "([value] IS NULL) <> ([protected_value] IS NULL)"));
        builder.HasKey(setting => setting.Id);

        builder.Property(setting => setting.Key).HasMaxLength(PlatformSetting.MaxKeyLength).IsRequired();
        builder.Property(setting => setting.Category).HasConversion<byte>();
        builder.Property(setting => setting.ValueType).HasConversion<byte>();
        builder.Property(setting => setting.Description).HasMaxLength(PlatformSetting.MaxDescriptionLength).IsRequired();

        builder.HasIndex(setting => setting.Key).IsUnique().HasDatabaseName("ux_platform_settings_key");

        builder.HasGymOSRowVersion();
    }
}
```

`DataProtectionKeyConfiguration.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

/// <summary>
/// The Data Protection key ring (M7 spec §3). In the database rather than on disk because UAT scales
/// to zero and a replica that comes back on a fresh file system must still decrypt yesterday's row.
/// Until Key Vault protects it (AZURE.md §7) the ring sits beside the ciphertext — stated there.
/// </summary>
public sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("data_protection_keys", "platform");
        builder.HasKey(key => key.Id);
        builder.Property(key => key.FriendlyName).HasMaxLength(200);
    }
}
```

`AppDbContext`: add `using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;`, make the class `: DbContext, IDataProtectionKeyContext`, and add `public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();`. `ApplyClientAssignedKeys` only touches Guid keys, so the identity `int` key keeps its store generation.

`PlatformAuditedEntities.All`: add

```csharp
        // What the platform is configured to do, and who changed it. The protected column is always
        // redacted; the plain column is the whole point of auditing this table (M7 spec §3).
        AuditedEntity.Of<PlatformSetting>("platform_setting", alwaysRedacted: [nameof(PlatformSetting.ProtectedValue)]),
```

`ConcurrencyModelTests.ExemptFromConcurrency`: add `[typeof(DataProtectionKey)] = "ASP.NET Data Protection's own key store: append-only XML rows written by the framework's key manager, never edited"` with its `using`.

- [ ] **Step 7: Build, unit tests, migration, transcription**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.Platform.Infrastructure.UnitTests && dotnet test tests/GymOS.ArchitectureTests`
Expected: clean; `ConcurrencyModelTests` sees `PlatformSetting` protected by its row version and `DataProtectionKey` exempted; `ForbiddenPackagesTests` passes.

Then:

```bash
dotnet ef migrations add AddPlatformSettings --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi
dotnet ef migrations has-pending-model-changes --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi
dotnet ef migrations script AddJobRuns AddPlatformSettings --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi --no-build
```

The migration must contain exactly: `platform.platform_settings` (columns `id, key, category, value_type, is_sensitive, value, protected_value, description, updated_by, created_at_utc, updated_at_utc, row_version`, unique index `ux_platform_settings_key`, check constraint `ck_platform_settings_one_value`) and `platform.data_protection_keys` (`id int identity, friendly_name nvarchar(200) null, xml nvarchar(max) null`). Anything else → stop and report BLOCKED. Transcribe both into `database/GymOS.Database/Tables/platform/platform_settings.sql` and `data_protection_keys.sql` in the style of `job_runs.sql` (header comment, `CREATE TABLE`, `GO`, indexes), using the constraint names the script printed; `dotnet build database/GymOS.Database/GymOS.Database.sqlproj` must succeed.

Run `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~ApiEndpointTests"` → PASS (migration applies on the fixture).

- [ ] **Step 8: Format and commit — everything in one commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Platform/GymOS.Platform.Domain/Settings/PlatformSetting.cs src/Platform/GymOS.Platform.Infrastructure database/GymOS.Database/Tables/platform Directory.Packages.props src/Host/GymOS.WebApi/GymOS.WebApi.csproj docs/Engineering/DEPENDENCY_POLICY.md tests/GymOS.Platform.Infrastructure.UnitTests/ConcurrencyModelTests.cs tests/GymOS.Platform.Domain.UnitTests/Settings/PlatformSettingTests.cs
git commit -m "settings: platform_settings and the Data Protection key ring — aggregate, tables, migration, packages

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 3: Permissions — `settings.read`, `settings.manage`, `audit.export`

**Files:**
- Modify: `src/Platform/GymOS.Platform.Domain/Identity/PlatformPermissions.cs`, `SystemRoles.cs`

**Interfaces:**
- Produces: `PlatformPermissions.SettingsRead = "settings.read"`, `SettingsManage = "settings.manage"`, `AuditExport = "audit.export"`.

- [ ] **Step 1: Add the constants and catalog entries**

After `MonitoringRead`:

```csharp
    /// <summary>
    /// Read the settings surface (§3.10) with sensitive values masked. Granted to PlatformAdmin and
    /// ReadOnly: a masked read carries no secret.
    /// </summary>
    public const string SettingsRead = "settings.read";

    /// <summary>
    /// Change settings, test the SMTP connection, toggle maintenance mode. <b>SuperAdmin only</b>: the
    /// category holds SMTP credentials, the lockout policy and the token lifetime — platform-level
    /// authority, the same reasoning that keeps <c>billing.manage</c> from PlatformAdmin. The maintenance
    /// middleware treats holding this permission as "may pass while maintenance is on" (M7 spec §6).
    /// </summary>
    public const string SettingsManage = "settings.manage";

    /// <summary>
    /// Export the audit log as a file. Separate from <see cref="AuditRead"/> and withheld from
    /// SupportAgent: a bulk export of who did what, with emails and IP addresses, is personal data
    /// leaving the system, and the person who may read a screen is not automatically the person who
    /// may walk out with the table (M7 spec §8).
    /// </summary>
    public const string AuditExport = "audit.export";
```

In `All`, after the `MonitoringRead` entry:

```csharp
        new(SettingsRead, ModuleName, "View platform settings, sensitive values masked"),
        new(SettingsManage, ModuleName, "Change platform settings, test email, toggle maintenance mode"),
        new(AuditExport, ModuleName, "Export the audit log as a file"),
```

- [ ] **Step 2: Grants**

`SystemRoles.Definitions`: PlatformAdmin gains `PlatformPermissions.SettingsRead` and `PlatformPermissions.AuditExport`; ReadOnly gains `PlatformPermissions.SettingsRead`; SupportAgent and BillingAdmin gain nothing; SuperAdmin is implicit. One-line comments: `// M7: masked reads carry no secret.` and `// M7: export is personal data leaving the system; PlatformAdmin, not SupportAgent.`

- [ ] **Step 3: Tests and commit**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~Role"`
Expected: PASS.

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Platform/GymOS.Platform.Domain/Identity
git commit -m "identity: settings.read, settings.manage (SuperAdmin only) and audit.export

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 4: Application abstractions, `SettingsRepository`, `DataProtectionSettingProtector`, `SettingsProvider`, DI

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Settings/Abstractions/ISettingsRepository.cs`, `ISettingsProvider.cs`, `ISettings.cs`, `ISettingProtector.cs`, `ISettingsValidator.cs`, `ISmtpConnectionTester.cs`, `SettingsContracts.cs`
- Create: `src/Platform/GymOS.Platform.Application/Settings/MaintenanceOptions.cs`, `RateLimitOptions.cs`, `SettingErrors.cs` (first member only)
- Create: `src/Platform/GymOS.Platform.Infrastructure/Settings/SettingsRepository.cs`, `DataProtectionSettingProtector.cs`, `SettingsProvider.cs`, `SettingsProviderOptions.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformSettingsServiceCollectionExtensions.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs` (call `AddPlatformSettings`; Key Vault wiring)
- Test: `tests/GymOS.IntegrationTests/SettingsProviderTests.cs`

**Interfaces:**
- Produces (namespace `GymOS.Platform.Application.Settings.Abstractions` unless noted):
  ```csharp
  public enum SettingSource { Default = 1, Configuration = 2, Database = 3 }
  public sealed record SettingView(string Key, SettingCategory Category, SettingValueType ValueType, bool IsSensitive, string Description,
      string? Value, SettingSource Source, bool Valid, string? ValidationMessage, Guid? UpdatedBy, DateTimeOffset? UpdatedAtUtc);
  public sealed record SettingValueWrite(string Key, string? Value);
  public sealed record SmtpConnectionTestResult(bool Ok, string? Stage, string? Server, string? Error);   // Stage: "Connect" | "Tls" | "Authenticate"
  public sealed record EffectiveSetting(SettingDefinition Definition, string? Value, SettingSource Source, Guid? UpdatedBy, DateTimeOffset? UpdatedAtUtc);
  public interface ISettingsRepository { Task<IReadOnlyList<PlatformSetting>> ListAllAsync(CancellationToken ct); Task<PlatformSetting?> FindByKeyAsync(string key, CancellationToken ct); void Add(PlatformSetting setting); void Remove(PlatformSetting setting); }
  public interface ISettingProtector { string Protect(string key, string plaintext); string Unprotect(string key, string ciphertext); }
  public interface ISettingsProvider
  {
      Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct);       // every definition, decrypted, from cache
      Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct);   // ConfigurationPath -> ToConfigurationValue(value) for DATABASE-sourced rows only
      void Invalidate();
  }
  public interface ISettings<T> where T : class, new() { T Current { get; } }
  public interface ISettingsValidator { Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken ct); } // Task 5 implements
  public interface ISmtpConnectionTester { Task<SmtpConnectionTestResult> TestAsync(CancellationToken ct); }                                                      // Task 8 implements
  // namespace GymOS.Platform.Application.Settings
  public sealed class MaintenanceOptions { public const string SectionName = "Maintenance"; public bool Enabled { get; set; } public string Message { get; set; } = "GymOS is undergoing maintenance. Please try again shortly."; }
  public sealed class RateLimitOptions { public const string SectionName = "Security:RateLimiting"; [Range(1, 1000)] public int SignInAttemptsPerWindow { get; set; } = 10; [Range(1, 3600)] public int SignInWindowSeconds { get; set; } = 60; [Range(1, 100000)] public int ApiRequestsPerMinute { get; set; } = 600; [Range(1, 100000)] public int AnonymousRequestsPerMinute { get; set; } = 60; }
  public static class SettingErrors { public static Error InvalidState(string message) => Error.Validation("settings.invalid_state", message); }
  // Infrastructure
  public static IServiceCollection AddPlatformSettings(this IServiceCollection services, IConfiguration configuration);
  ```
  Data Protection is registered inside `AddPlatformSettings` (`AddDataProtection().SetApplicationName("GymOS").PersistKeysToDbContext<AppDbContext>()`); the **Key Vault protector is wired in `Program.cs`** where the Azure packages are referenced.

- [ ] **Step 1: Write the failing integration test**

```csharp
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Resolution order and secrecy through the real host on real SQL Server (M7 spec §3–4): no row →
/// configuration → default; a sensitive value round-trips through Data Protection with the key ring in
/// the database; a fresh scope decrypts what another encrypted.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SettingsProviderTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;

    public SettingsProviderTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("settings_provider");
        _host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Jobs:Enabled"] = "false";
            settings["Email:Smtp:Host"] = "configured.example.test"; // the configuration layer
        });
        using HttpClient client = _host.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    private static EffectiveSetting Find(IReadOnlyList<EffectiveSetting> all, string key) =>
        all.Single(setting => setting.Definition.Key == key);

    [Fact]
    public async Task ResolutionIsRowThenConfigurationThenDefault()
    {
        using IServiceScope scope = _host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ISettingsProvider>();

        IReadOnlyList<EffectiveSetting> all = await provider.GetEffectiveAsync(CancellationToken.None);
        Assert.Equal(("configured.example.test", SettingSource.Configuration), (Find(all, "email.smtp_host").Value, Find(all, "email.smtp_host").Source));
        Assert.Equal(("1025", SettingSource.Default), (Find(all, "email.smtp_port").Value, Find(all, "email.smtp_port").Source));
        Assert.Equal(("15", SettingSource.Default), (Find(all, "security.lockout_minutes").Value, Find(all, "security.lockout_minutes").Source));

        await SeedRowAsync("email.smtp_host", plain: "row.example.test");
        provider.Invalidate();
        all = await provider.GetEffectiveAsync(CancellationToken.None);

        Assert.Equal(("row.example.test", SettingSource.Database), (Find(all, "email.smtp_host").Value, Find(all, "email.smtp_host").Source));
        IReadOnlyDictionary<string, string?> overlay = await provider.GetOverlayAsync(CancellationToken.None);
        Assert.Equal("row.example.test", overlay["Email:Smtp:Host"]);
        Assert.False(overlay.ContainsKey("Email:Smtp:Port")); // only database rows overlay
    }

    [Fact]
    public async Task ASensitiveValueIsStoredAsCiphertextAndReadBackInTheClear()
    {
        using (IServiceScope scope = _host.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISettingProtector>();
            string ciphertext = protector.Protect("email.smtp_password", "s3cret");
            Assert.NotEqual("s3cret", ciphertext);
            await SeedRowAsync("email.smtp_password", protectedValue: ciphertext);
        }

        await using (AppDbContext context = _host.CreateDbContext())
        {
            PlatformSetting row = await context.Set<PlatformSetting>().AsNoTracking().SingleAsync(s => s.Key == "email.smtp_password");
            Assert.Null(row.Value);
            Assert.DoesNotContain("s3cret", row.ProtectedValue, StringComparison.Ordinal);
            Assert.True(await context.DataProtectionKeys.AnyAsync()); // the ring is in the database
        }

        using IServiceScope second = _host.Services.CreateScope();
        var provider = second.ServiceProvider.GetRequiredService<ISettingsProvider>();
        provider.Invalidate();
        IReadOnlyList<EffectiveSetting> all = await provider.GetEffectiveAsync(CancellationToken.None);
        Assert.Equal("s3cret", Find(all, "email.smtp_password").Value);
    }

    private async Task SeedRowAsync(string key, string? plain = null, string? protectedValue = null)
    {
        Assert.True(PlatformSettingDefinitions.TryGet(key, out SettingDefinition definition));
        await using AppDbContext context = _host.CreateDbContext();
        context.Set<PlatformSetting>().Add(PlatformSetting.Create(Guid.NewGuid(), definition, plain, protectedValue, null));
        await context.SaveChangesAsync();
    }
}
```

`_host.Services` — `AuthenticationHost` exposes the factory's service provider (M6 added a test that resolves from it; find the member's actual name with `grep -n "Services" tests/GymOS.IntegrationTests/AuthenticationHost.cs` and use it).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~SettingsProviderTests"`
Expected: build error.

- [ ] **Step 3: Abstractions and options classes**

Write the seven abstraction files, the two options classes and `SettingErrors` exactly as the Interfaces block declares, each type with a one-paragraph doc comment stating its role from the spec (`ISettingsProvider`: "per-instance cache, TTL `Settings:CacheTtlSeconds`, `Invalidate()` after every write; other replicas wait out the TTL"; `ISettingProtector`: "purpose `GymOS.PlatformSettings.v1`, sub-purpose the key, so a ciphertext cannot be moved to another key's row"). `RateLimitOptions` needs `using System.ComponentModel.DataAnnotations;`.

- [ ] **Step 4: Infrastructure**

`SettingsProviderOptions.cs`: `public sealed class SettingsProviderOptions { public const string SectionName = "Settings"; [Range(1, 3600)] public int CacheTtlSeconds { get; set; } = 30; }`.

`SettingsRepository.cs` — `internal sealed class SettingsRepository : ISettingsRepository` over `AppDbContext`: `ListAllAsync` = `Set<PlatformSetting>().ToListAsync(ct)` (tracked — writers mutate what they load); `FindByKeyAsync` = `SingleOrDefaultAsync(s => s.Key == key, ct)`; `Add`/`Remove` on the set.

`DataProtectionSettingProtector.cs`:

```csharp
using GymOS.Platform.Application.Settings.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>Data Protection with the key as a sub-purpose: a ciphertext cannot be moved to another key's row (M7 spec §3).</summary>
internal sealed class DataProtectionSettingProtector : ISettingProtector
{
    public const string Purpose = "GymOS.PlatformSettings.v1";

    private readonly IDataProtectionProvider _provider;

    public DataProtectionSettingProtector(IDataProtectionProvider provider) => _provider = provider;

    public string Protect(string key, string plaintext) => _provider.CreateProtector(Purpose, key).Protect(plaintext);

    public string Unprotect(string key, string ciphertext) => _provider.CreateProtector(Purpose, key).Unprotect(ciphertext);
}
```

`SettingsProvider.cs` — `internal sealed partial class SettingsProvider : ISettingsProvider`, **a singleton** (the cache is per process) taking `IServiceScopeFactory`, `IConfiguration`, `IClock`, `IOptions<SettingsProviderOptions>`, `ILogger<SettingsProvider>`:

- Holds `Snapshot? _snapshot` — `private sealed record Snapshot(DateTimeOffset LoadedAt, IReadOnlyList<EffectiveSetting> Effective, IReadOnlyDictionary<string, string?> Overlay)` — guarded by a `SemaphoreSlim(1, 1)` for the reload.
- `LoadAsync`: open a scope, resolve `ISettingsRepository` and `ISettingProtector`, `ListAllAsync`, index rows by key. For every definition in `PlatformSettingDefinitions.All`: if a row exists → value = `definition.IsSensitive ? protector.Unprotect(key, row.ProtectedValue!) : row.Value`, source `Database`, and when `ConfigurationPath` is not null `overlay[path] = definition.ToConfigurationValue(value)`; else if `ConfigurationPath` is not null and `_configuration[path]` is non-empty → value = `definition.FromConfigurationValue(raw)`, source `Configuration`; else value = `definition.Default`, source `Default`. `UpdatedBy`/`UpdatedAtUtc` come from the row when there is one. A row whose key has no definition is skipped with `LogOrphanRow` (Warning). An `Unprotect` failure (`System.Security.Cryptography.CryptographicException`) logs `LogUnprotectFailed` (Error) and treats the key as having no row.
- `GetEffectiveAsync` / `GetOverlayAsync`: if `_snapshot` is null or `LoadedAt + TTL < _clock.UtcNow` → reload under the semaphore (double-check after acquiring); return from the snapshot. The overlay dictionary instance is **the same reference** for the life of a snapshot (binders use reference equality to skip rebinding). If reload throws and a previous snapshot exists → `LogReloadFailed` (Error), keep serving it; if none exists → rethrow.
- `Invalidate()` sets `_snapshot = null`.

`PlatformSettingsServiceCollectionExtensions.cs`:

```csharp
public static IServiceCollection AddPlatformSettings(this IServiceCollection services, IConfiguration configuration)
{
    ArgumentNullException.ThrowIfNull(services);
    ArgumentNullException.ThrowIfNull(configuration);

    services.AddOptions<SettingsProviderOptions>().Bind(configuration.GetSection(SettingsProviderOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
    services.AddOptions<MaintenanceOptions>().Bind(configuration.GetSection(MaintenanceOptions.SectionName)).ValidateOnStart();
    services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

    // The key ring lives in the database so a replica scaled from zero still decrypts (M7 spec §3).
    // Key Vault protection of the ring is wired in Program.cs, where the Azure packages live.
    services.AddDataProtection().SetApplicationName("GymOS").PersistKeysToDbContext<AppDbContext>();

    services.TryAddScoped<ISettingsRepository, SettingsRepository>();
    services.TryAddScoped<ISettingProtector, DataProtectionSettingProtector>();
    services.TryAddSingleton<ISettingsProvider, SettingsProvider>();

    return services;
}
```

`Program.cs`: after `AddPlatformMonitoring(builder.Configuration)`:

```csharp
    // M7 settings: the declared catalog over platform_settings, and Data Protection for sensitive values.
    builder.Services.AddPlatformSettings(builder.Configuration);

    // Key Vault wraps the key ring only when the owner has created the vault and set the key id
    // (AZURE.md §7). Inert otherwise — the config value is the switch.
    if (builder.Configuration["DataProtection:KeyVaultKeyId"] is { Length: > 0 } keyVaultKeyId)
    {
        builder.Services.AddDataProtection().ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), new DefaultAzureCredential());
    }
```

with `using Azure.Identity;` and `using Microsoft.AspNetCore.DataProtection;`.

- [ ] **Step 5: Run**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~SettingsProviderTests|FullyQualifiedName~ApiEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Platform/GymOS.Platform.Application/Settings src/Platform/GymOS.Platform.Infrastructure/Settings src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformSettingsServiceCollectionExtensions.cs src/Host/GymOS.WebApi/Program.cs tests/GymOS.IntegrationTests/SettingsProviderTests.cs
git commit -m "settings: ISettingsProvider over rows, configuration and defaults, with Data Protection for sensitive values

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 5: `ISettings<T>` binder, `OptionsSettingsValidator`, consumer switch, `SwitchingEmailSender`

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Settings/SettingsBinder.cs`, `OptionsSettingsValidator.cs`, `SwitchingEmailSender.cs`; `tests/GymOS.TestingSupport/FakeSettings.cs`
- Modify: `PlatformSettingsServiceCollectionExtensions.cs` (registrations), `PlatformIdentityServiceCollectionExtensions.cs` (email sender), and the nine consumers: Infrastructure `Email/SmtpEmailSender.cs`, `Email/ActivationLinks.cs`, `Identity/JwtTokenService.cs`, `Monitoring/JobRunMaintainer.cs`, `Persistence/Seeding/SuperAdminSeeder.cs`; Application `Identity/SignIn/SignInCommandHandler.cs`, `Identity/SessionFactory.cs`, `Identity/Activation/SetPasswordCommand.cs`, `Identity/ChangePassword/ChangePasswordCommandHandler.cs`, `Monitoring/MonitoringQueriesHandlers.cs`
- Modify tests that construct those by hand or register `IOptions<…>` for them (the compiler lists them; known: `RetentionJobTests`, `JobRunMaintainerTests`, `MonitoringQueryHandlerTests`, `IdentityTestContext`, `GymTestContext`)
- Test: `tests/GymOS.Platform.Infrastructure.UnitTests/SettingsCatalogBindingTests.cs`; two more cases in `SettingsProviderTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record SettingsBinding<T>(string SectionName) where T : class, new();
  internal sealed partial class SettingsBinder<T> : ISettings<T> where T : class, new()   // Current; (T? Value, string? Failures) Bind(overlay)
  internal sealed class OptionsSettingsValidator : ISettingsValidator
  internal sealed class SwitchingEmailSender : IEmailSender
  public static IServiceCollection AddBoundSettings<T>(this IServiceCollection services, string sectionName) where T : class, new();
  public sealed class FakeSettings<T> : ISettings<T> where T : class, new() { public FakeSettings(T current); public T Current { get; set; } }   // TestingSupport
  ```

- [ ] **Step 1: Write the catalog-binding unit test**

```csharp
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>Every catalog path lands on a real options property, and durations overlay as the TimeSpan their property expects.</summary>
public sealed class SettingsCatalogBindingTests
{
    [Fact]
    public void EveryConfigurationPathBindsToAKnownOptionsProperty()
    {
        Dictionary<string, string?> overlay = PlatformSettingDefinitions.All
            .Where(d => d.ConfigurationPath is not null)
            .ToDictionary(d => d.ConfigurationPath!, d => d.ToConfigurationValue(d.Default ?? (d.ValueType == SettingValueType.Int ? "1" : "x")));
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(overlay).Build();

        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var login = configuration.GetSection(LoginPolicyOptions.SectionName).Get<LoginPolicyOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var rate = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var maintenance = configuration.GetSection(MaintenanceOptions.SectionName).Get<MaintenanceOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var monitoring = configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>(o => o.ErrorOnUnknownConfiguration = true)!;

        Assert.Equal(1025, email.Smtp.Port);
        Assert.Equal(TimeSpan.FromMinutes(15), login.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(7), login.RefreshTokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.AccessTokenLifetime);
        Assert.Equal(60, rate.SignInWindowSeconds);
        Assert.False(maintenance.Enabled);
        Assert.Equal(30, monitoring.JobRunRetentionDays);
    }
}
```

`ErrorOnUnknownConfiguration` makes a misspelt path fail loudly. The `JwtOptions` section also contains the signing key in real configuration; here the in-memory source holds only catalog paths, so `Get` sees nothing unknown.

- [ ] **Step 2: Run it**

Run: `dotnet test tests/GymOS.Platform.Infrastructure.UnitTests --filter "FullyQualifiedName~SettingsCatalogBindingTests"`
Expected: PASS already (it pins Task 1's catalog against the options classes). If it fails, a `ConfigurationPath` is wrong: fix the catalog.

- [ ] **Step 3: `SettingsBinder<T>` and `AddBoundSettings<T>`**

```csharp
using GymOS.Platform.Application.Settings.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>Where a bound <typeparamref name="T"/> reads its section from — one registration per T.</summary>
public sealed record SettingsBinding<T>(string SectionName) where T : class, new();

/// <summary>
/// <see cref="ISettings{T}"/> over the merged layers (M7 spec §4): the real configuration with the
/// provider's database overlay on top, bound into the existing options class and run through its
/// existing validators. On validation failure the last valid value keeps being served and the failure
/// is logged — a platform whose email settings are broken must keep signing people in.
/// Scoped: <see cref="Current"/> is stable within one request; the provider behind it is the singleton cache.
/// </summary>
internal sealed partial class SettingsBinder<T> : ISettings<T> where T : class, new()
{
    private readonly ISettingsProvider _provider;
    private readonly IConfiguration _configuration;
    private readonly SettingsBinding<T> _binding;
    private readonly IEnumerable<IValidateOptions<T>> _validators;
    private readonly ILogger<SettingsBinder<T>> _logger;
    private T? _lastValid;
    private IReadOnlyDictionary<string, string?>? _boundOverlay;

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Settings for {Options} failed validation and the previous values remain in effect: {Failures}")]
    private static partial void LogInvalid(ILogger logger, string options, string failures);

    public SettingsBinder(
        ISettingsProvider provider, IConfiguration configuration, SettingsBinding<T> binding,
        IEnumerable<IValidateOptions<T>> validators, ILogger<SettingsBinder<T>> logger)
    {
        _provider = provider; _configuration = configuration; _binding = binding; _validators = validators; _logger = logger;
    }

    public T Current
    {
        get
        {
            // A blocking read inside a getter is tolerable only because the provider serves from its
            // cache; the miss path is one query per TTL, not per call.
            IReadOnlyDictionary<string, string?> overlay = _provider.GetOverlayAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (_lastValid is not null && ReferenceEquals(overlay, _boundOverlay))
            {
                return _lastValid;
            }

            (T? bound, string? failures) = Bind(overlay);
            _boundOverlay = overlay;
            if (bound is not null)
            {
                _lastValid = bound;
                return bound;
            }

            LogInvalid(_logger, typeof(T).Name, failures ?? "unknown");
            return _lastValid ?? throw new OptionsValidationException(_binding.SectionName, typeof(T), [failures ?? "invalid"]);
        }
    }

    /// <summary>Binds and validates a proposed overlay without touching the cache — the write path's check.</summary>
    public (T? Value, string? Failures) Bind(IReadOnlyDictionary<string, string?> overlay)
    {
        IConfigurationRoot merged = new ConfigurationBuilder()
            .AddConfiguration(_configuration)
            .AddInMemoryCollection(overlay) // later wins: rows over configuration
            .Build();

        var instance = new T();
        merged.GetSection(_binding.SectionName).Bind(instance);

        List<string> failures = [];
        foreach (IValidateOptions<T> validator in _validators)
        {
            ValidateOptionsResult result = validator.Validate(Options.DefaultName, instance);
            if (result.Failed)
            {
                failures.AddRange(result.Failures ?? [result.FailureMessage ?? "invalid"]);
            }
        }

        return failures.Count == 0 ? (instance, null) : (null, string.Join("; ", failures));
    }
}
```

In `PlatformSettingsServiceCollectionExtensions`:

```csharp
public static IServiceCollection AddBoundSettings<T>(this IServiceCollection services, string sectionName) where T : class, new()
{
    services.TryAddSingleton(new SettingsBinding<T>(sectionName));
    services.TryAddScoped<ISettings<T>, SettingsBinder<T>>();
    return services;
}
```

and in `AddPlatformSettings`, after the provider registration: `AddBoundSettings<EmailOptions>(EmailOptions.SectionName)`, `<LoginPolicyOptions>`, `<JwtOptions>`, `<MonitoringOptions>`, `<MaintenanceOptions>`, `<RateLimitOptions>` — each with its `SectionName` constant.

- [ ] **Step 4: `OptionsSettingsValidator`**

```csharp
internal sealed class OptionsSettingsValidator : ISettingsValidator
{
    private readonly IServiceProvider _services;

    public OptionsSettingsValidator(IServiceProvider services) => _services = services;

    public Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken cancellationToken)
    {
        string? failures = category switch
        {
            SettingCategory.General or SettingCategory.Email => Check<EmailOptions>(overlay),
            SettingCategory.Security => Check<LoginPolicyOptions>(overlay) ?? Check<JwtOptions>(overlay) ?? Check<RateLimitOptions>(overlay),
            SettingCategory.Maintenance => Check<MaintenanceOptions>(overlay),
            SettingCategory.Retention => Check<MonitoringOptions>(overlay),
            _ => null,
        };

        return Task.FromResult(failures is null ? Result.Success() : Result.Failure(SettingErrors.InvalidState(failures)));
    }

    private string? Check<T>(IReadOnlyDictionary<string, string?> overlay) where T : class, new() =>
        ((SettingsBinder<T>)_services.GetRequiredService<ISettings<T>>()).Bind(overlay).Failures;
}
```

Register `services.TryAddScoped<ISettingsValidator, OptionsSettingsValidator>();`.

- [ ] **Step 5: `SwitchingEmailSender` and the email registration**

```csharp
/// <summary>
/// Picks the sender per call from the live Email settings (M7 spec §4): SMTP when enabled, the logging
/// sender otherwise. Replaces the composition-time choice that read Email:Enabled once at start-up.
/// </summary>
internal sealed class SwitchingEmailSender : IEmailSender
{
    private readonly ISettings<EmailOptions> _settings;
    private readonly SmtpEmailSender _smtp;
    private readonly LoggingEmailSender _logging;

    public SwitchingEmailSender(ISettings<EmailOptions> settings, SmtpEmailSender smtp, LoggingEmailSender logging)
    {
        _settings = settings; _smtp = smtp; _logging = logging;
    }

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken) =>
        _settings.Current.Enabled ? _smtp.SendAsync(message, cancellationToken) : _logging.SendAsync(message, cancellationToken);
}
```

In `PlatformIdentityServiceCollectionExtensions`, delete the `emailEnabled` if/else and register `services.TryAddScoped<SmtpEmailSender>(); services.TryAddScoped<LoggingEmailSender>(); services.TryAddScoped<IEmailSender, SwitchingEmailSender>();`. `SmtpEmailSender` and `ActivationLinks` take `ISettings<EmailOptions>` instead of `IOptions<EmailOptions>`; read `Current` once into a local at the top of each method that uses it. `LoggingEmailSender` stays in `SmtpEmailSender.cs`.

- [ ] **Step 6: Switch the remaining consumers**

`SignInCommandHandler`, `SessionFactory`, `SetPasswordCommand`'s handler, `ChangePasswordCommandHandler`, `SuperAdminSeeder`: `IOptions<LoginPolicyOptions>` → `ISettings<LoginPolicyOptions>`, `_policy.Value` → `_policy.Current`. `JwtTokenService`: `IOptions<JwtOptions>` → `ISettings<JwtOptions>`, `.Value` → `.Current` (the signing key still comes through configuration; the overlay never names it). `JobRunMaintainer`, `GetMonitoringOverviewQueryHandler`, `GetJobStatesQueryHandler`: `IOptions<MonitoringOptions>` → `ISettings<MonitoringOptions>`. `JwtBearerOptionsPostConfiguration` keeps `IOptions<JwtOptions>`.

Create `tests/GymOS.TestingSupport/FakeSettings.cs`:

```csharp
using GymOS.Platform.Application.Settings.Abstractions;

namespace GymOS.TestingSupport;

public sealed class FakeSettings<T> : ISettings<T> where T : class, new()
{
    public FakeSettings(T current) => Current = current;

    public T Current { get; set; }
}
```

(`GymOS.TestingSupport` may need a project reference to `GymOS.Platform.Application`; add it if missing.) Then fix every test the compiler names: `Options.Create(new X())` → `new FakeSettings<X>(new X())`; `services.AddSingleton<IOptions<X>>(...)` → `services.AddSingleton<ISettings<X>>(new FakeSettings<X>(...))`.

- [ ] **Step 7: Two more integration cases**

Append to `SettingsProviderTests`:

```csharp
    [Fact]
    public async Task ABoundOptionsClassSeesARowWithoutARestart()
    {
        using (IServiceScope first = _host.Services.CreateScope())
        {
            Assert.Equal(5, first.ServiceProvider.GetRequiredService<ISettings<LoginPolicyOptions>>().Current.MaxFailedAttempts);
        }

        await SeedRowAsync("security.max_failed_attempts", plain: "3");
        _host.Services.GetRequiredService<ISettingsProvider>().Invalidate();

        using IServiceScope next = _host.Services.CreateScope();
        Assert.Equal(3, next.ServiceProvider.GetRequiredService<ISettings<LoginPolicyOptions>>().Current.MaxFailedAttempts);
    }

    [Fact]
    public async Task AnInvalidRowKeepsTheLastValidValuesInForce()
    {
        using IServiceScope scope = _host.Services.CreateScope();
        var email = scope.ServiceProvider.GetRequiredService<ISettings<EmailOptions>>();
        Assert.False(email.Current.Enabled); // the valid bind this scope will fall back to

        // Enabling email with a console URL that is not absolute fails EmailOptions' own validator.
        await SeedRowAsync("email.enabled", plain: "true");
        await SeedRowAsync("general.console_base_url", plain: "not a url");
        _host.Services.GetRequiredService<ISettingsProvider>().Invalidate();

        Assert.False(email.Current.Enabled); // last valid, not the broken row
    }
```

- [ ] **Step 8: Run everything touched**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.Platform.Application.UnitTests && dotnet test tests/GymOS.Platform.Infrastructure.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~SettingsProviderTests|FullyQualifiedName~RetentionJobTests|FullyQualifiedName~JobRunMaintainerTests|FullyQualifiedName~Auth|FullyQualifiedName~Activation|FullyQualifiedName~ApiEndpointTests|FullyQualifiedName~MonitoringApiTests"`
Expected: all PASS.

- [ ] **Step 9: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add -A src tests
git commit -m "settings: ISettings<T> binds rows over configuration into the existing options classes; consumers change type only

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 6: The write pipeline — `SettingsWriter`, `GetSettingsQuery`, `UpdateSettingsCommand`

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Settings/SettingsWriter.cs`, `SettingsQueries.cs`, `SettingsCommands.cs`; extend `SettingErrors.cs`
- Test: `tests/GymOS.Platform.Application.UnitTests/SettingsCommandTests.cs` (+ `Fakes/SettingsFakes.cs`)

**Interfaces:**
- Consumes: Task 4's abstractions, Task 5's `ISettingsValidator`.
- Produces (namespace `GymOS.Platform.Application.Settings`):
  ```csharp
  public static class SettingErrors
  {
      public static Error InvalidState(string message);                 // settings.invalid_state
      public static Error UnknownKey(string key);                        // settings.unknown_key
      public static Error WrongCategory(string key, SettingCategory c);  // settings.wrong_category
      public static Error InvalidValue(string key, string rule);         // settings.invalid_value, message "`{key}` {rule}."
      public static Error UseMaintenanceEndpoint { get; }                // settings.use_maintenance_endpoint
      public static Error EmailDisabled { get; }                         // settings.email_disabled
      public static Error ConfirmationRequired { get; }                  // settings.confirmation_required
      public static Error ConfirmationMismatch { get; }                  // settings.confirmation_mismatch
  }
  [RequiresPermission(PlatformPermissions.SettingsRead)]   public sealed record GetSettingsQuery(SettingCategory? Category) : IQuery<Result<IReadOnlyList<SettingView>>>;
  [RequiresPermission(PlatformPermissions.SettingsManage)] public sealed record UpdateSettingsCommand(SettingCategory Category, IReadOnlyList<SettingValueWrite> Values) : ICommand<Result<IReadOnlyList<SettingView>>>;
  public sealed class SettingsWriter   // shared by UpdateSettings and SetMaintenanceMode (Task 9)
  {
      public Task<Result<IReadOnlyList<SettingView>>> ApplyAsync(SettingCategory category, IReadOnlyList<SettingValueWrite> values, CancellationToken ct);
  }
  public static class SettingViews { public static SettingView From(EffectiveSetting effective, bool valid = true, string? validationMessage = null); }  // masks sensitive values
  ```

- [ ] **Step 1: Write the failing tests**

`Fakes/SettingsFakes.cs`:

```csharp
internal sealed class FakeSettingsRepository : ISettingsRepository
{
    public List<PlatformSetting> Rows { get; } = [];
    public Task<IReadOnlyList<PlatformSetting>> ListAllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<PlatformSetting>>(Rows.ToList());
    public Task<PlatformSetting?> FindByKeyAsync(string key, CancellationToken ct) => Task.FromResult(Rows.SingleOrDefault(r => r.Key == key));
    public void Add(PlatformSetting setting) => Rows.Add(setting);
    public void Remove(PlatformSetting setting) => Rows.Remove(setting);
}

/// <summary>Resolves from the fake repository the way the real provider does, minus the cache — so a test sees its own writes.</summary>
internal sealed class FakeSettingsProvider : ISettingsProvider
{
    private readonly FakeSettingsRepository _rows; private readonly ISettingProtector _protector;
    public FakeSettingsProvider(FakeSettingsRepository rows, ISettingProtector protector) { _rows = rows; _protector = protector; }
    public int Invalidations { get; private set; }
    public Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EffectiveSetting>>(PlatformSettingDefinitions.All.Select(d =>
        {
            PlatformSetting? row = _rows.Rows.SingleOrDefault(r => r.Key == d.Key);
            return row is null
                ? new EffectiveSetting(d, d.Default, SettingSource.Default, null, null)
                : new EffectiveSetting(d, d.IsSensitive ? _protector.Unprotect(d.Key, row.ProtectedValue!) : row.Value, SettingSource.Database, row.UpdatedBy, row.UpdatedAtUtc);
        }).ToList());
    public async Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct) =>
        (await GetEffectiveAsync(ct)).Where(e => e.Source == SettingSource.Database && e.Definition.ConfigurationPath is not null)
            .ToDictionary(e => e.Definition.ConfigurationPath!, e => e.Definition.ToConfigurationValue(e.Value!));
    public void Invalidate() => Invalidations++;
}

internal sealed class FakeSettingProtector : ISettingProtector
{
    public string Protect(string key, string plaintext) => $"enc({key}:{plaintext})";
    public string Unprotect(string key, string ciphertext) => ciphertext[(5 + key.Length)..^1];
}

internal sealed class FakeSettingsValidator : ISettingsValidator
{
    public string? RejectWith { get; set; }
    public IReadOnlyDictionary<string, string?>? LastOverlay { get; private set; }
    public Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken ct)
    { LastOverlay = overlay; return Task.FromResult(RejectWith is null ? Result.Success() : Result.Failure(SettingErrors.InvalidState(RejectWith))); }
}
```

`SettingsCommandTests.cs` — a dispatcher fixture on the `GymTestContext` pattern (`AddCqrsDispatcher`, `AddAuthorizationBehavior`, `AddPlatformApplication`, the four fakes above as singletons, `FakeUnitOfWork`, `FakeCurrentUser`/`FakePermissionEvaluator`/`FakeAuditTrail`, `FakeClock`, `NullLogger<>`), `ActingAs(PlatformPermissions.SettingsManage)`, and these cases:

```csharp
    [Fact] public async Task AnUnknownKeyIsRefusedAndNothingIsWritten()          // "email.nope" → settings.unknown_key, Rows empty, SaveChanges not called
    [Fact] public async Task AKeyFromAnotherCategoryIsRefused()                  // Email batch containing "security.lockout_minutes" → settings.wrong_category
    [Fact] public async Task ABadValueNamesTheKeyAndTheRule()                    // "email.smtp_port" = "70000" → settings.invalid_value, message contains "email.smtp_port" and "between 1 and 65535"
    [Fact] public async Task TheMaskLeavesASensitiveRowUntouched()               // seed protected row; PUT value "••••" → row's ProtectedValue unchanged, UpdatedBy unchanged
    [Fact] public async Task AnEmptyStringOrNullDeletesTheRow()                  // seed plain row; PUT "" → removed; seed again; PUT null → removed
    [Fact] public async Task ANewSensitiveValueIsProtectedBeforeItIsStored()     // PUT "email.smtp_password" = "hunter2" → Rows single has Value null, ProtectedValue "enc(email.smtp_password:hunter2)"
    [Fact] public async Task TheValidatorSeesTheWouldBeOverlayAndCanRejectTheBatch() // validator.RejectWith = "Smtp:Host required"; PUT enabled=true → settings.invalid_state with that message; Rows empty; validator.LastOverlay["Email:Enabled"] == "true"
    [Fact] public async Task TheMaintenanceCategoryIsRefusedHere()               // Category Maintenance → settings.use_maintenance_endpoint
    [Fact] public async Task ASuccessfulWriteReturnsTheCategoryMaskedAndInvalidatesTheCache() // PUT host + password → result has both keys, password Value == "••••", Source Database; provider.Invalidations == 1; SaveChanges called once
    [Fact] public async Task GetReturnsEveryDefinitionInTheCategoryWithItsSource() // ActingAs(SettingsRead); GetSettingsQuery(Email) → 10 views; smtp_port Source Default Value "1025"
    [Fact] public async Task ReadNeedsSettingsReadAndWriteNeedsSettingsManage()  // ActingAs(SettingsRead) then Update → PermissionDeniedException
```

Write each body out in full: seed via `repo.Rows.Add(PlatformSetting.Create(...))`, dispatch through `IDispatcher`, assert on `Result.Error.Code`, on `repo.Rows`, on `unitOfWork.SaveChangesCalls` (check `FakeUnitOfWork`'s counter name in `IdentityFakes.cs`).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests --filter "FullyQualifiedName~SettingsCommandTests"`
Expected: build error.

- [ ] **Step 3: `SettingErrors`, `SettingViews`, `SettingsWriter`**

`SettingErrors` — the eight members with codes as listed; messages: UnknownKey `$"No setting named '{key}' is defined."`, WrongCategory `$"'{key}' does not belong to the {category} category."`, InvalidValue `$"`{key}` {rule}."`, UseMaintenanceEndpoint `"Maintenance settings change through POST /settings/maintenance-mode, which requires confirmation."`, EmailDisabled `"Email is disabled; enable it and save before testing the connection."`, ConfirmationRequired `"Send X-Confirm-Action: maintenance to change maintenance mode."`, ConfirmationMismatch `"X-Confirm-Action must be exactly 'maintenance'."` (Validation kind for all).

`SettingViews.From(effective, valid, message)`: `Value = effective.Definition.IsSensitive && effective.Value is not null ? PlatformSettingDefinitions.Mask : effective.Value`, the rest copied.

`SettingsWriter` (public sealed, registered scoped by `AddPlatformApplication`'s scan or explicitly — check how `SuperAdminGuard` is registered and follow it):

```csharp
public sealed class SettingsWriter
{
    private readonly ISettingsRepository _rows; private readonly ISettingsProvider _provider; private readonly ISettingProtector _protector;
    private readonly ISettingsValidator _validator; private readonly IUnitOfWork _unitOfWork; private readonly ICurrentUser _currentUser;

    public SettingsWriter(ISettingsRepository rows, ISettingsProvider provider, ISettingProtector protector, ISettingsValidator validator, IUnitOfWork unitOfWork, ICurrentUser currentUser) { … }

    public async Task<Result<IReadOnlyList<SettingView>>> ApplyAsync(SettingCategory category, IReadOnlyList<SettingValueWrite> values, CancellationToken ct)
    {
        // 1–2: definitions, categories, types and ranges — before any row is touched.
        var resolved = new List<(SettingDefinition Definition, string? Value)>();
        foreach (SettingValueWrite write in values)
        {
            if (!PlatformSettingDefinitions.TryGet(write.Key, out SettingDefinition definition)) return Fail(SettingErrors.UnknownKey(write.Key));
            if (definition.Category != category) return Fail(SettingErrors.WrongCategory(write.Key, category));
            if (write.Value is not null && write.Value.Length > 0 && !(definition.IsSensitive && write.Value == PlatformSettingDefinitions.Mask))
            {
                string? rule = definition.ValidateValue(write.Value);
                if (rule is not null) return Fail(SettingErrors.InvalidValue(write.Key, rule));
            }
            resolved.Add((definition, write.Value));
        }

        // 5: the would-be overlay — current database rows with this batch applied — through the options validators.
        IReadOnlyList<EffectiveSetting> current = await _provider.GetEffectiveAsync(ct);
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (EffectiveSetting e in current.Where(e => e.Source == SettingSource.Database && e.Definition.ConfigurationPath is not null))
            overlay[e.Definition.ConfigurationPath!] = e.Definition.ToConfigurationValue(e.Value!);
        foreach ((SettingDefinition d, string? v) in resolved.Where(r => r.Definition.ConfigurationPath is not null))
        {
            if (d.IsSensitive && v == PlatformSettingDefinitions.Mask) continue;                 // unchanged
            if (string.IsNullOrEmpty(v)) overlay.Remove(d.ConfigurationPath!);                   // back to the layer below
            else overlay[d.ConfigurationPath!] = d.ToConfigurationValue(v);
        }
        Result validation = await _validator.ValidateAsync(category, overlay, ct);
        if (validation.IsFailure) return Fail(validation.Error!);

        // 3–4, 6: rows.
        foreach ((SettingDefinition d, string? v) in resolved)
        {
            PlatformSetting? row = await _rows.FindByKeyAsync(d.Key, ct);
            if (d.IsSensitive && v == PlatformSettingDefinitions.Mask) continue;
            if (string.IsNullOrEmpty(v)) { if (row is not null) _rows.Remove(row); continue; }
            string? plain = d.IsSensitive ? null : v;
            string? protectedValue = d.IsSensitive ? _protector.Protect(d.Key, v) : null;
            if (row is null) _rows.Add(PlatformSetting.Create(Guid.NewGuid(), d, plain, protectedValue, _currentUser.UserId));
            else row.Set(plain, protectedValue, _currentUser.UserId);
        }
        await _unitOfWork.SaveChangesAsync(ct);   // ConcurrencyConflictException → 409 by the existing handler
        _provider.Invalidate();                     // 7

        IReadOnlyList<EffectiveSetting> after = await _provider.GetEffectiveAsync(ct);
        return Result.Success<IReadOnlyList<SettingView>>(after.Where(e => e.Definition.Category == category).Select(e => SettingViews.From(e)).ToList());
    }

    private static Result<IReadOnlyList<SettingView>> Fail(Error error) => Result.Failure<IReadOnlyList<SettingView>>(error);
}
```

Empty values (`""`) and `null` both mean delete, per the Global Constraints — that is why `string.IsNullOrEmpty(v)` is the test in both loops.

- [ ] **Step 4: Query and command handlers**

`SettingsQueries.cs`: `GetSettingsQueryHandler` → `provider.GetEffectiveAsync`, filter by category when given, map through `SettingViews.From`. For `Valid`/`ValidationMessage`: run `_validator.ValidateAsync(category, currentOverlay)` per distinct category present and mark every view of a failing category `Valid = false` with the message — this is how the console sees a broken row.

`SettingsCommands.cs`: `UpdateSettingsCommandHandler` → `command.Category == SettingCategory.Maintenance ? Fail(SettingErrors.UseMaintenanceEndpoint) : _writer.ApplyAsync(...)`. Leave room in this file for Task 8's and Task 9's commands.

- [ ] **Step 5: Run, format, commit**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests && dotnet test tests/GymOS.ArchitectureTests`
Expected: PASS (the architecture tests check every command/query carries a permission attribute).

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Platform/GymOS.Platform.Application/Settings tests/GymOS.Platform.Application.UnitTests
git commit -m "settings: the write pipeline — validate the batch, validate the would-be options, then write, then invalidate

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 7: `GET /settings`, `PUT /settings`, API tests, OpenAPI snapshot

**Files:**
- Create: `src/Host/GymOS.WebApi/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs` (`app.MapSettingsEndpoints()` after `MapMonitoringEndpoints()`), `tests/GymOS.IntegrationTests/ApiEndpointTests.cs` (tripwire 60 → 62), `contracts/openapi-v1.json` (regenerated)
- Test: `tests/GymOS.IntegrationTests/SettingsApiTests.cs`

**Interfaces:**
- Produces: `GET /api/v1/settings?category=` (`settings.read`, `Produces<IReadOnlyList<SettingView>>`), `PUT /api/v1/settings` (`settings.manage`, body `UpdateSettingsRequest(SettingCategory Category, IReadOnlyList<SettingValueWrite> Values)`, `Produces<IReadOnlyList<SettingView>>`, problems 400/401/403/409). Pattern: `AnalyticsEndpoints` / `MonitoringEndpoints` (versioned group `/api/v{version:apiVersion}/settings`, tag "Settings", `IDispatcher`, `result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem()`). Descriptions state: the mask, "empty or null deletes", "Maintenance changes through its own endpoint", "`source` says which layer answered".

- [ ] **Step 1: Write the failing API tests** (fixture like `MonitoringApiTests`: isolated database, `Jobs:Enabled=false`, SuperAdmin signed in; `SystemClock` if time is needed):

```csharp
    [Fact] public async Task TheCatalogIsServedWithSourcesAndTheMask()      // GET ?category=Email → 10 items; smtp_port value "1025" source "Default"; smtp_password value null (no row)
    [Fact] public async Task APutRoundTripsPlainValuesAndMasksSensitiveOnes() // PUT {Email:[smtp_host=row.example.test, smtp_password=hunter2, smtp_port=2525]} → 200; GET shows host, "••••", "2525", all source "Database"
    [Fact] public async Task TheMaskOnPutLeavesTheSecretUnchanged()           // PUT password "••••" → 200; DB row ProtectedValue identical to before (read via _host.CreateDbContext())
    [Fact] public async Task AnEmptyValueReturnsTheKeyToTheLayerBelow()       // PUT smtp_port "" → GET shows "1025" source "Default"
    [Fact] public async Task AnInvalidStateIs400NamingTheRule()               // PUT {Email:[enabled=true, from_address=""]} with no host row and no configured host → 400, problem detail contains "Smtp:Host is required"
    [Fact] public async Task TheMaintenanceCategoryIs400Here()                // PUT {Maintenance:[enabled=true]} → 400 code settings.use_maintenance_endpoint
    [Fact] public async Task PlatformAdminMayReadButNotWrite()                // PlatformApiHelpers.SignedInAsync(..., "PlatformAdmin", ...) → GET 200, PUT 403
    [Fact] public async Task ReadOnlyMayReadAndSupportAgentMayNot()           // ReadOnly GET 200; SupportAgent GET 403
    [Fact] public async Task TheAuditLogRecordsThePlainValueAndRedactsTheSecret() // after the round-trip PUT: GET /api/v1/audit-logs?entityType=platform_setting → entries; the smtp_password entry's detail newValues contains "***REDACTED***" and not "hunter2"; the smtp_host entry's newValues contains "row.example.test"
```

Serialize `SettingCategory` as its name (check the host's JSON options — enums may already serialize as strings via `JsonStringEnumConverter`; if not, add `[JsonConverter(typeof(JsonStringEnumConverter))]` on the two enums in Task 1's files and say so in the report).

- [ ] **Step 2: Run to verify they fail (404s)**, **Step 3: write the endpoints**, **Step 4: wire `Program.cs`**, **Step 5: run the tests**, **Step 6: bump the tripwire to 62 and regenerate the snapshot** (`UPDATE_OPENAPI_SNAPSHOT=1` on the `OpenApiContractTests` filter; read the diff: two new paths, new schemas `SettingView`, `SettingValueWrite`, `UpdateSettingsRequest`, `SettingCategory`, `SettingValueType`, `SettingSource`; nothing removed; contract test green without the variable).

- [ ] **Step 7: Format and commit**

```bash
dotnet format GymOS.Backend.slnx --verify-no-changes || dotnet format GymOS.Backend.slnx
git add src/Host tests/GymOS.IntegrationTests/SettingsApiTests.cs tests/GymOS.IntegrationTests/ApiEndpointTests.cs contracts/openapi-v1.json
git commit -m "settings: GET and PUT /settings — the catalog with sources, masked secrets, batch writes per category

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 8: `POST /settings/test-email` — the SMTP connection test

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Settings/MailKitSmtpConnectionTester.cs`; `tests/GymOS.IntegrationTests/FakeSmtpServer.cs`
- Modify: `SettingsCommands.cs` (`TestEmailConnectionCommand`), `PlatformSettingsServiceCollectionExtensions.cs` (register tester), `SettingsEndpoints.cs`, `ApiEndpointTests.cs` (62 → 63), `contracts/openapi-v1.json`
- Test: `tests/GymOS.IntegrationTests/SettingsApiTests.cs` (three cases)

**Interfaces:**
- Produces: `[RequiresPermission(PlatformPermissions.SettingsManage)] public sealed record TestEmailConnectionCommand : ICommand<Result<SmtpConnectionTestResult>>;` — handler: `if (!_email.Current.Enabled) return EmailDisabled; result = await _tester.TestAsync(ct); _auditTrail.Record("settings.email_tested", details: { ["ok"] = result.Ok.ToString(), ["stage"] = result.Stage, ["error"] = result.Error }); await _unitOfWork.SaveChangesAsync(ct); return Success(result);`. Endpoint `POST /api/v1/settings/test-email` → 200 with the result (a failed test is a 200), 400 `settings.email_disabled`.
- `MailKitSmtpConnectionTester : ISmtpConnectionTester`:

```csharp
public async Task<SmtpConnectionTestResult> TestAsync(CancellationToken cancellationToken)
{
    EmailOptions options = _settings.Current;
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(15));
    using var client = new SmtpClient();
    string stage = "Connect";
    try
    {
        await client.ConnectAsync(options.Smtp.Host, options.Smtp.Port,
            options.Smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, timeout.Token);
        if (!string.IsNullOrWhiteSpace(options.Smtp.Username))
        {
            stage = "Authenticate";
            await client.AuthenticateAsync(options.Smtp.Username, options.Smtp.Password ?? string.Empty, timeout.Token);
        }
        await client.NoOpAsync(timeout.Token);
        string server = client.Capabilities.ToString(); // or the greeting if MailKit exposes it; record what you used
        await client.DisconnectAsync(quit: true, timeout.Token);
        return new SmtpConnectionTestResult(true, null, server, null);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
    catch (OperationCanceledException) { return new SmtpConnectionTestResult(false, stage, null, "Timed out after 15 seconds."); }
    catch (SslHandshakeException exception) { return new SmtpConnectionTestResult(false, "Tls", null, exception.Message); }
    catch (Exception exception) when (exception is SocketException or IOException or MailKit.ProtocolException or MailKit.Security.AuthenticationException)
    { return new SmtpConnectionTestResult(false, stage, null, exception.Message); }
}
```

`FakeSmtpServer` (test helper, `IAsyncDisposable`): a `TcpListener` on `127.0.0.1:0` that, per connection, writes `220 fake ESMTP\r\n`, answers `EHLO`/`HELO` with `250-fake\r\n250 AUTH PLAIN LOGIN\r\n`, `AUTH …` with `235 ok\r\n` when a `RejectAuth` flag is false and `535 bad credentials\r\n` when true, `NOOP` with `250 ok\r\n`, `QUIT` with `221 bye\r\n`; exposes `Port`. About 60 lines; no external image.

- [ ] Tests (in `SettingsApiTests`): `TestEmailConnectsAuthenticatesAndReports` (start fake server; PUT Email {enabled=true, smtp_host=127.0.0.1, smtp_port=<port>, smtp_username=u, smtp_password=p, from_address=a@b.test, from_display_name=GymOS}; POST test-email → 200 `ok: true`; audit row `settings.email_tested` with `ok=True`), `ARefusedPasswordReportsTheAuthenticateStage` (`RejectAuth = true` → 200 `ok: false`, `stage: "Authenticate"`), `AClosedPortReportsConnect` (port of a listener you closed → `stage: "Connect"`), `TestEmailIs400WhenEmailIsDisabled`.

- [ ] Run, tripwire 63, snapshot, format, commit:

```bash
git commit -m "settings: POST /settings/test-email tests the SMTP connection and sends nothing — ADR-53 stands

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 9: Maintenance mode — command, endpoint, middleware

**Files:**
- Create: `src/Host/GymOS.WebApi/Middleware/MaintenanceModeMiddleware.cs`
- Modify: `SettingsCommands.cs` (`SetMaintenanceModeCommand`), `SettingsEndpoints.cs`, `Program.cs` (middleware between `UseAuthentication()` and `UseAuthorization()`), `ApiEndpointTests.cs` (63 → 64), `contracts/openapi-v1.json`
- Test: `tests/GymOS.IntegrationTests/MaintenanceModeTests.cs`

**Interfaces:**
- Produces: `[RequiresPermission(PlatformPermissions.SettingsManage)] public sealed record SetMaintenanceModeCommand(bool Enabled, string? Message, string? Confirmation) : ICommand<Result<IReadOnlyList<SettingView>>>;` — handler: `Confirmation` null/blank → `SettingErrors.ConfirmationRequired`; not exactly `"maintenance"` (ordinal, trimmed) → `ConfirmationMismatch`; then `_writer.ApplyAsync(SettingCategory.Maintenance, [new("maintenance.enabled", Enabled ? "true" : "false"), new("maintenance.message", string.IsNullOrWhiteSpace(Message) ? null : Message)], ct)`; on success `_auditTrail.Record("settings.maintenance_mode_changed", details: { ["enabled"] = Enabled.ToString(), ["message"] = Message })` + `SaveChangesAsync`. Endpoint `POST /api/v1/settings/maintenance-mode` with `.RequiresConfirmAction()` (from `GymOS.WebApi.Extensions`), body `SetMaintenanceModeRequest(bool Enabled, string? Message)`, confirmation read with `ConfirmationHeaders.ConfirmAction(httpContext.Request)`, 200 with the Maintenance views, 400 on confirmation errors.
- `MaintenanceModeMiddleware`:

```csharp
public sealed class MaintenanceModeMiddleware
{
    public const int RetryAfterSeconds = 300;
    private static readonly PathString[] AlwaysOpen = ["/health", "/version"];
    private readonly RequestDelegate _next;
    public MaintenanceModeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ISettings<MaintenanceOptions> maintenance, IPermissionEvaluator permissions)
    {
        MaintenanceOptions current = maintenance.Current;
        if (!current.Enabled || IsAlwaysOpen(context.Request) || await permissions.HasPermissionAsync(PlatformPermissions.SettingsManage, context.RequestAborted))
        {
            await _next(context);
            return;
        }

        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            title: "Maintenance in progress",
            detail: current.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "settings.maintenance",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "settings.maintenance" })
            .ExecuteAsync(context);
    }

    private static bool IsAlwaysOpen(HttpRequest request) =>
        AlwaysOpen.Any(prefix => request.Path.StartsWithSegments(prefix))
        || (HttpMethods.IsPost(request.Method)
            && (request.Path.Equals("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals("/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase)));
}
```

`IPermissionEvaluator` is scoped and DB-backed with a per-request snapshot cache (`CallerSnapshot`), so the check costs one query per request **only while maintenance is on and only for authenticated callers**; anonymous callers short-circuit inside the evaluator (`UserId is null → []`). `Program.cs`: `app.UseAuthentication(); app.UseMiddleware<MaintenanceModeMiddleware>(); app.UseAuthorization();` with a comment naming the spec section.

- [ ] Tests (`MaintenanceModeTests`, fixture like `MonitoringApiTests`):
  `TogglingWithoutTheHeaderIs400`, `TogglingWithTheWrongHeaderIs400Mismatch`, `WhenOnAnonymousAndOtherRolesGet503WithRetryAfter` (toggle on with header; anonymous `GET /api/v1/monitoring/overview` → 503, `Retry-After: 300`, problem `detail` == the message; a ReadOnly session's `GET /api/v1/settings` → 503), `WhenOnSuperAdminHealthAndSignInStillPass` (SuperAdmin GET settings 200; `/health/ready` 200; `/version` 200; `POST /auth/login` for the ReadOnly user 200), `TurningItOffRestoresService`, `TheToggleIsAudited` (`settings.maintenance_mode_changed` present with `enabled=True`), `PutSettingsCannotToggleIt` (already in Task 7; keep one assertion here that PUT Maintenance is 400 even with the header).

- [ ] Run, tripwire 64, snapshot, format, commit:

```bash
git commit -m "settings: maintenance mode — a confirmed toggle, and a middleware that lets health, sign-in and SuperAdmins through

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 10: Rate limiting from settings

**Files:**
- Modify: `src/Host/GymOS.WebApi/Extensions/RateLimitingExtensions.cs`, `Program.cs` (signature unchanged: `AddGymOSRateLimiting(builder.Configuration)` may drop the parameter — keep the call compiling)
- Test: `tests/GymOS.IntegrationTests/RateLimitingTests.cs`

**Interfaces:**
- Produces: `RateLimitingExtensions.SignInPolicy` (unchanged name) and a **global limiter** on `options.GlobalLimiter`:

```csharp
options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
{
    PathString path = httpContext.Request.Path;
    if (path.StartsWithSegments("/health") || path.Equals("/version"))
    {
        return RateLimitPartition.GetNoLimiter("open");
    }

    RateLimitOptions limits = httpContext.RequestServices.GetRequiredService<ISettings<RateLimitOptions>>().Current;
    string? userId = httpContext.User.FindFirstValue("sub");
    return userId is not null
        ? RateLimitPartition.GetSlidingWindowLimiter($"user:{userId}", _ => new SlidingWindowRateLimiterOptions
            { PermitLimit = limits.ApiRequestsPerMinute, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 })
        : RateLimitPartition.GetSlidingWindowLimiter($"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}", _ => new SlidingWindowRateLimiterOptions
            { PermitLimit = limits.AnonymousRequestsPerMinute, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6, QueueLimit = 0 });
});
```

and the sign-in policy's factory reading `limits.SignInAttemptsPerWindow` / `TimeSpan.FromSeconds(limits.SignInWindowSeconds)` from the same `ISettings<RateLimitOptions>` per request. **Important:** `UseRateLimiter()` runs before `UseAuthentication()` in the current pipeline, so `httpContext.User` is empty at partition time and every request would be "anonymous". Move `app.UseRateLimiter()` to **after** `app.UseAuthentication()` (and before the maintenance middleware) so the per-user partition sees the principal; the sign-in policy is unaffected. Note the factory caches limiter instances per partition key — a changed setting applies to **new** partitions immediately and to existing ones when their window rolls; document that in the class comment. Add `OnRejected` problem details with `type: security.rate_limited` alongside the existing `Retry-After`.

- [ ] Tests (`RateLimitingTests`, host created with `settings["Security:RateLimiting:ApiRequestsPerMinute"] = "3"`): `TheFourthAuthenticatedCallInAMinuteIs429WithRetryAfter` (three `GET /api/v1/monitoring/jobs` 200, fourth 429 with `Retry-After`), `HealthIsNeverLimited` (10 × `/health/ready` all 200), `ASavedSettingRaisesTheLimitWithoutARestart` (SuperAdmin PUTs `security.api_requests_per_minute = 100` as a fresh user via `SignedInAsync` so the partition is new; that user's 10 calls all 200), `SignInIsStillLimitedPerAddress` (11 bad logins → the 11th is 429).

- [ ] Run, format, commit:

```bash
git commit -m "security: a global rate limiter — per user when signed in, per address otherwise — with every number a live setting

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 11: Audit export — `StreamAsync`, `AuditCsv`, query, endpoint

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Auditing/AuditCsv.cs`, `ExportAuditLogs.cs`
- Modify: `Auditing/IAuditLogQueries.cs` (`AuditLogExportFilter`, `StreamAsync`), `Persistence/Queries/AuditLogQueries.cs`, `Endpoints/AuditLogEndpoints.cs`, `ApiEndpointTests.cs` (64 → 65), `contracts/openapi-v1.json`
- Test: `tests/GymOS.Platform.Application.UnitTests/AuditCsvTests.cs`, `tests/GymOS.IntegrationTests/AuditExportTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AuditLogExportFilter(Guid? UserId, string? Action, string? EntityType, Guid? EntityId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);
  IAsyncEnumerable<AuditLogListItem> StreamAsync(AuditLogExportFilter filter, CancellationToken ct);   // on IAuditLogQueries; ascending created_at_utc then id; AsNoTracking().AsAsyncEnumerable()
  public static class AuditCsv { public const string Header = "..."; public static string FormatRow(AuditLogListItem item); public static string Escape(string? field); public static int MaxRangeDays = 366; }
  [RequiresPermission(PlatformPermissions.AuditExport)] public sealed record ExportAuditLogsQuery(Guid? UserId, string? Action, string? EntityType, Guid? EntityId, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc) : IQuery<Result<AuditLogExport>>;
  public sealed record AuditLogExport(AuditLogExportFilter Filter, IAsyncEnumerable<AuditLogListItem> Rows);
  [RequiresPermission(PlatformPermissions.AuditExport)] public sealed record RecordAuditExportCommand(AuditLogExportFilter Filter, long RowCount) : ICommand<Result>;   // IAuditTrail.Record("audit.exported", details: filter fields + rowCount) + SaveChanges
  ```
  Validation in the query handler: `FromUtc`/`ToUtc` both required (`audit.export_range`, "`from` and `to` are required"), `from <= to`, `to - from <= 366 days` (same code). `Escape`: always quote; `"` → `""`; if the first char is one of `= + - @ \t \r` prefix `'`. `FormatRow`: the nine columns in the constrained order, `created_at_utc` as ISO-8601 round-trip (`"o"`), Guids as `D`, CRLF terminator.

- [ ] **Unit tests (`AuditCsvTests`)**: header text exact; a row with a comma and a quote in `user_email` is quoted and doubled; a leading `=` is prefixed; null fields become `""`; CRLF; date format round-trips.

- [ ] **Endpoint** in `AuditLogEndpoints`:

```csharp
group.MapGet("/export", ExportAsync)
    .RequireAuthorization(PlatformPermissions.AuditExport)
    .WithName("ExportAuditLogs")
    .WithSummary("Stream the audit trail as CSV")
    .WithDescription("`from` and `to` are required, inclusive, and at most 366 days apart. Same filters as the list, no paging; rows stream oldest first. Nine columns; `oldValues`/`newValues` are not exported — read them per entry. The export itself is recorded in the audit log with the filter and the row count.")
    .Produces(StatusCodes.Status200OK, contentType: "text/csv")
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

private static async Task<IResult> ExportAsync(IDispatcher dispatcher, CancellationToken ct, Guid? userId = null, string? action = null, string? entityType = null, Guid? entityId = null, DateTimeOffset? from = null, DateTimeOffset? to = null)
{
    Result<AuditLogExport> result = await dispatcher.QueryAsync(new ExportAuditLogsQuery(userId, action, entityType, entityId, from, to), ct);
    if (result.IsFailure) return result.ToProblem();
    AuditLogExport export = result.Value;
    string fileName = $"audit-logs-{export.Filter.FromUtc:yyyyMMdd}-{export.Filter.ToUtc:yyyyMMdd}.csv";
    return Results.Stream(async stream =>
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.NewLine = "\r\n";
        await writer.WriteLineAsync(AuditCsv.Header);
        long count = 0;
        await foreach (AuditLogListItem item in export.Rows.WithCancellation(ct)) { await writer.WriteLineAsync(AuditCsv.FormatRow(item)); count++; }
        await writer.FlushAsync(ct);
        await dispatcher.SendAsync(new RecordAuditExportCommand(export.Filter, count), ct);
    }, "text/csv; charset=utf-8", fileName);
}
```

The `IDispatcher` and the scoped DbContext are alive for the duration of the response body because the request scope is; the audit row is written **after** the stream completes so the count is real (spec §8).

- [ ] **Integration tests (`AuditExportTests`)**: seed 1,200 `AuditLog` rows across two days via `_host.CreateDbContext()` (one with `UserEmail` containing `,` and `"`, one with `Action` starting `=`); `GET /export?from=&to=` → 200, `text/csv`, `Content-Disposition` attachment, 1,201 lines, ascending, quoted field intact, `'=` prefix present; `SupportAgentIs403`; `A400DayRangeIs400`; `MissingFromIs400`; `TheExportIsAudited` (`audit.exported` row, `rowCount` = 1200).

- [ ] Run, tripwire 65, snapshot (one path, `text/csv` response), format, commit:

```bash
git commit -m "audit: GET /audit-logs/export streams the trail as CSV behind audit.export, and records itself

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 12: Docs, full suite, STATUS

**Files:**
- Create: `docs/BusinessRules/Settings.md`
- Modify: `docs/BusinessRules/README.md` (index row), `docs/RELEASE-NOTES.md` (`## M7 — Settings & Hardening`, suggested version `0.9.0`), `docs/Infrastructure/AZURE.md` (§7: the Data Protection key ring, the standing gap, the Key Vault commands), `docs/STATUS.md`

- [ ] **Step 1: Full suite once**: `dotnet test GymOS.Backend.slnx` — record the total for STATUS (expect a longer integration run than M6's; the CI budget is 40 minutes).
- [ ] **Step 2: `Settings.md`** on `Monitoring.md`'s shape: Purpose; 1 definitions are code, rows are values (the catalog table); 2 resolution order and sources; 3 secrecy (two columns, Data Protection, the ring, the gap); 4 the write path (all seven steps, mask semantics, invalid state); 5 typed consumption and live reload (TTL, last-valid-on-failure); 6 test-email is a connection test (ADR-53); 7 maintenance mode (who passes, 503 shape); 8 rate limiting (three policies, per replica); 9 audit export (columns, escaping, range, permission); 10 endpoints table with codes; 11 permissions and their reasoning; 12 deferred; 13 known limitations (TTL staleness across replicas; limiter partitions keep old numbers until the window rolls; export has no row cap beyond the date window; the key ring beside the ciphertext until Key Vault; a blocking read inside `ISettings<T>.Current`).
- [ ] **Step 3: AZURE.md §7** — new item: "Data Protection key ring: `platform.data_protection_keys`, unprotected until Key Vault. To close: `az keyvault create …`, `az identity …` (the Container App's system-assigned identity), `az keyvault set-policy --key-permissions get unwrapKey wrapKey`, set `DataProtection__KeyVaultKeyId=https://<vault>.vault.azure.net/keys/<name>/<version>` on the app; verify with `az containerapp show … --query properties.template.containers[0].env`." Mark as owner action, like items 4 and 5.
- [ ] **Step 4: Release notes and README index**; **Step 5: STATUS**: replace the M7-spec resume paragraph with "M7 implemented at the head of `development` (commits `<first>..<last>`), suite total, look at the CI run for the push, **next: the owed list** (the ~19 findings, gym-scoped-grants spec review, M6's constructor-throw follow-up, the integration teardown cost, Key Vault as the owner's action)"; add `### M7 — what landed` with the file map in prose and every deviation found while building (the `SignInWindowSeconds` path; anything else the ledger records).
- [ ] **Step 6: Commit (do not push — the controller pushes after the final review):**

```bash
git add docs
git commit -m "docs: M7 settings & hardening — business rules, release notes, AZURE.md key-ring gap, STATUS

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

## Self-review

**Spec coverage.** §1 deliverables → Tasks 1–11; deferred items appear only in docs (Task 12). §2 catalog, types, durations, never-settings, validation-by-options-class → Tasks 1, 5, 6. §3 table with two deviations, aggregate, audited entity, rows-only-when-set, key ring, purpose string, migration + transcription, packages → Tasks 2, 4. §4 provider, cache/TTL, `ISettings<T>` over existing classes, last-valid-on-failure, the nine consumers → Tasks 4, 5. §5 PUT pipeline in order, mask/empty/null, validator, 409 via existing handler, Maintenance refused; test-email as connection test with audit; maintenance toggle with confirmation and audit → Tasks 6, 7, 8, 9. §6 middleware placement, exemptions, SuperAdmin pass (as `settings.manage`), 503 shape → Task 9. §7 three policies, numbers live, health unlimited, 429 + Retry-After, per replica → Task 10. §8 export → Task 11. §9 permissions and grants → Task 3; endpoints → 7, 8, 9, 11. §11 verification: every listed check has a test above; the "live" checks are the executor's option.

**Placeholders.** Tasks 7–11 list test cases by name with one-line bodies and give the production code in full; the executor writes the test bodies from the named assertions and the fixture pattern already in the repository (`MonitoringApiTests`). That is deliberate: the bodies are the same shape as the M6 tests the implementer can read. No TBDs.

**Type consistency.** `SettingView` (11 fields) identical in Tasks 4, 6, 7. `EffectiveSetting` in Tasks 4, 6. `ISettingsProvider` members in Tasks 4, 5, 6. `SettingsBinder<T>.Bind` used by `OptionsSettingsValidator` (Task 5). `SettingErrors` members in Tasks 4, 6, 8, 9. `RateLimitOptions` property names match the catalog paths (`SignInWindowSeconds`, `ApiRequestsPerMinute`, `AnonymousRequestsPerMinute`) in Tasks 1, 4, 10. `AuditLogExportFilter` in Task 11 only. Tripwire sequence 60 → 62 → 63 → 64 → 65 across Tasks 7, 8, 9, 11.

**Known open question for the executor's controller.** `SettingsBinder<T>` casts `ISettings<T>` to `SettingsBinder<T>` inside the validator; a test that registers `FakeSettings<T>` and also resolves `ISettingsValidator` would break that cast — the validator is only registered in Infrastructure, and Application tests use `FakeSettingsValidator`, so no such combination exists; if one appears, expose `Bind` through a small `ISettingsBinder<T>` interface instead.
