# M7 — Settings & Hardening — Design

**Status: approved 2026-09-19.** Four decisions taken with the owner: sensitive values are protected
with **ASP.NET Data Protection, key ring in the database, Key Vault protection when the vault exists**;
`POST /settings/test-email` **tests the SMTP connection and sends no message**, so ADR-53's two-message
limit stands; in maintenance mode **SuperAdmins pass and everyone else gets 503**; and settings reach
their consumers through **an explicit `ISettingsProvider` with typed `ISettings<T>`**, the shape AD-9 and
the addendum's §5 both name, not through a configuration provider.

**Frozen scope** (ARCHITECTURE.md §2.7 `platform_settings`, §3.9 audit export, §3.10 settings, AD-6
step-up confirmation, AD-7 non-entity audit, AD-9 settings as data with cached provider, roadmap item
8; ARCHITECTURE-ADDENDUM.md §5 / AD-45 as the target this must remain a subset of), narrowed by the
2026-09-03 decision recorded in STATUS.md: **M7's settings module starts with the categories that have a
consumer today** — General, Email, Security, Maintenance and the retention windows. Storage, AI and
Payment arrive as additive definitions with the providers they configure.

## 1. What M7 delivers, and what it defers

| Delivered now | Deferred, and to what |
|---|---|
| `platform.platform_settings`, a code-declared definitions catalog, `ISettingsProvider`, typed `ISettings<T>` | Tenant and gateway scopes, `setting_definitions` as a table, JSON Schema — AD-45's layered service, when a second scope exists |
| Categories General, Email, Security, Maintenance, **Retention** | Branding, Storage, AI, Payment — each with the provider it configures |
| Sensitive values under Data Protection, key ring in `platform.data_protection_keys` | Key Vault protection of that ring — one config value, once the owner creates the vault (AZURE.md §7) |
| `GET /settings`, `PUT /settings`, `POST /settings/test-email` (connection test), `POST /settings/maintenance-mode` | A real test message — would be ADR-53's third message |
| Maintenance-mode middleware | A console banner for `requires_restart` settings — nothing in M7 requires a restart |
| A global API rate limiter with settings-tunable numbers; the sign-in limiter's numbers move to settings | Distributed (cross-replica) limits — Redis or a shared store, when the platform runs more than one replica for real |
| `GET /audit-logs/export` as streamed CSV, with `audit.export` | — |
| — | `setting_value_history` — the audit log already answers who/when/what-before for every row |
| — | Outbox-driven cache invalidation across replicas — with the outbox processor; M7 uses a 30-second TTL |

**Why `Retention` is a category.** §2.7's category list has no home for the retention windows it names
in the same paragraph. The M6 retention window (`Monitoring:JobRunRetentionDays`) has a consumer today;
the audit-partition window is still counsel's. One category with one key is honest about that and is an
additive value in the enum.

## 2. Definitions are code; rows are values

`PlatformSettingDefinitions.All` (Platform.Domain.Settings) is the catalog, exactly as `PlatformPermissions.All`
is for permissions: **a value without a definition cannot be written** (400, `settings.unknown_key`), and
the catalog is what `GET /settings` enumerates — every definition appears, whether or not a row exists.

```csharp
public sealed record SettingDefinition(
    string Key,                 // "{category}.{name}", e.g. "email.smtp_host"
    SettingCategory Category,   // General, Email, Security, Maintenance, Retention (+ frozen: Branding, Storage, AI, Payment)
    SettingValueType ValueType, // String, Int, Bool, Json — §2.7's set, unchanged
    bool IsSensitive,
    string Description,
    string? Default,            // the code default, as text; null means "no value"
    string ConfigurationPath);  // the appsettings/env path this key overlays, e.g. "Email:Smtp:Host"
```

Durations are `Int` with the unit in the key (`security.lockout_minutes`), so §2.7's four value types
are enough and a console renders a number with a unit rather than parsing ISO-8601.

**The initial catalog** — every key has a consumer today, and its `ConfigurationPath` is the existing
options property it overlays, so current appsettings and Container App environment keep working with no
rows written:

| Category | Key | Type | Sensitive | Default | Overlays |
|---|---|---|---|---|---|
| General | `general.platform_name` | String | | `GymOS` | (new; the console header) |
| General | `general.console_base_url` | String | | `http://localhost:3000` | `Email:ConsoleBaseUrl` |
| Email | `email.enabled` | Bool | | `false` | `Email:Enabled` |
| Email | `email.smtp_host` | String | | — | `Email:Smtp:Host` |
| Email | `email.smtp_port` | Int | | `1025` | `Email:Smtp:Port` |
| Email | `email.smtp_use_starttls` | Bool | | `false` | `Email:Smtp:UseStartTls` |
| Email | `email.smtp_username` | String | | — | `Email:Smtp:Username` |
| Email | `email.smtp_password` | String | **yes** | — | `Email:Smtp:Password` |
| Email | `email.from_address` | String | | — | `Email:From:Address` |
| Email | `email.from_display_name` | String | | — | `Email:From:DisplayName` |
| Email | `email.invitation_lifetime_hours` | Int | | `72` | `Email:InvitationLifetimeHours` |
| Email | `email.password_reset_lifetime_hours` | Int | | `1` | `Email:PasswordResetLifetimeHours` |
| Security | `security.max_failed_attempts` | Int | | `5` | `Security:LoginPolicy:MaxFailedAttempts` |
| Security | `security.lockout_minutes` | Int | | `15` | `Security:LoginPolicy:LockoutDuration` |
| Security | `security.refresh_token_days` | Int | | `7` | `Security:LoginPolicy:RefreshTokenLifetime` |
| Security | `security.minimum_password_length` | Int | | `12` | `Security:LoginPolicy:MinimumPasswordLength` |
| Security | `security.access_token_minutes` | Int | | `15` | `Security:Jwt:AccessTokenLifetime` |
| Security | `security.sign_in_attempts_per_window` | Int | | `10` | `Security:RateLimiting:SignInAttemptsPerWindow` |
| Security | `security.sign_in_window_seconds` | Int | | `60` | `Security:RateLimiting:SignInWindow` |
| Security | `security.api_requests_per_minute` | Int | | `600` | (new) |
| Security | `security.anonymous_requests_per_minute` | Int | | `60` | (new) |
| Maintenance | `maintenance.enabled` | Bool | | `false` | (new) |
| Maintenance | `maintenance.message` | String | | `GymOS is undergoing maintenance. Please try again shortly.` | (new) |
| Retention | `retention.job_runs_days` | Int | | `30` | `Monitoring:JobRunRetentionDays` |

**Never settings:** the JWT signing key, issuer and audience, connection strings, the SuperAdmin seed
password, Argon2 parameters, `Jobs:Enabled`, `Monitoring:StaleGrace`. Each is either a secret whose
custody is the deployment's (ENVIRONMENTS.md §6), a value that changes what a token *means*, or a
per-process operational switch. A settings row is something an administrator changes from the console;
none of these should be.

**Validation lives with the definition and with the options class.** Each definition carries the
type check and a range where one exists (`email.smtp_port` 1–65535, lifetimes ≥ 1, limits ≥ 1). Then,
because the consumers are the *existing* options classes with their *existing* `IValidateOptions<T>`
chains (`EmailOptions` demands a host and a from-address when enabled; `LoginPolicyOptions` and the rest
carry data annotations), a `PUT` builds the would-be `T` from the merged layers and runs that chain
before writing anything. **Invalid writes are rejected, never persisted-then-broken** — the addendum's
own rule, and the reason the settings module owns no second copy of the email rules.

## 3. Persistence

**`platform.platform_settings`** — §2.7's table, with two deviations named:

| column | type | notes |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `key` | `nvarchar(100)` not null, unique | `ux_platform_settings_key` |
| `category` | `tinyint` not null | `SettingCategory` |
| `value_type` | `tinyint` not null | `SettingValueType`, copied from the definition at write so a row is self-describing |
| `is_sensitive` | `bit` not null | copied from the definition at write |
| `value` | `nvarchar(max)` null | plain text; **null for sensitive keys** |
| `protected_value` | `nvarchar(max)` null | Data Protection ciphertext; **null for non-sensitive keys** |
| `description` | `nvarchar(500)` not null | copied from the definition |
| `updated_by` | `uniqueidentifier` null | `ICurrentUser.UserId` |
| `updated_at_utc` | `datetimeoffset` not null | |
| `row_version` | `rowversion` | mutable entity — the repository rule (ENGINEERING_PLAYBOOK §3) |

**Deviation 1 — two value columns.** §2.7 has one `value` column "encrypted at rest" when sensitive. The
audit interceptor redacts by *property name* (`AuditPolicy.ShouldRedact`), not by row, so one column
would mean either every setting change is redacted from the audit log or ciphertext lands in it. Two
columns give the interceptor a name to redact always (`ProtectedValue`) and a name to record in plain
(`Value`), so "who changed the SMTP host from what to what" is answerable and the password never is.
Exactly one of the two is populated; a check constraint says so.

**Deviation 2 — `Retention`** as an additional category value (§1).

**`PlatformSetting`** is an aggregate root in Platform.Domain.Settings with the row-version token: it is
edited from the console and two administrators can race. `Set(string? plainValue, string? protectedValue,
Guid? updatedBy, DateTimeOffset now)` is its one mutation; the definition decides which of the two the
handler passes. Registered in `PlatformAuditedEntities` as `AuditedEntity.Of<PlatformSetting>("platform_setting",
alwaysRedacted: [nameof(PlatformSetting.ProtectedValue)])`.

**Rows exist only for values someone has set.** A read resolves each definition as **row → configuration
path → code default**, and reports which layer answered as `source: Database | Configuration | Default`.
Deleting a row (PUT with `null`) returns the key to the layer below; a console can therefore show
"reset to default" honestly, and a deploy with no rows behaves exactly as today.

**`platform.data_protection_keys`** — the Data Protection key ring (`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`'s
`DataProtectionKey`: `id int identity`, `friendly_name`, `xml`), configured through `AppDbContext`
implementing `IDataProtectionKeyContext`. The ring lives in the database because UAT scales to zero and
a replica that comes back with a fresh file system must still decrypt yesterday's row. **Until Key Vault
exists the ring is unprotected beside the ciphertext**, so a full database dump exposes sensitive
settings; that is written in AZURE.md §7 as the standing gap, with the one action that closes it:
create the vault, grant the Container App's managed identity `wrap/unwrap`, set
`DataProtection:KeyVaultKeyId`, and the host calls `ProtectKeysWithAzureKeyVault`. The ciphertext
format does not change when that happens — Data Protection re-wraps keys, not payloads.

**Purpose string** `GymOS.PlatformSettings.v1`. Protectors are created per row from the key, so a
ciphertext cannot be moved to another key's row: `CreateProtector("GymOS.PlatformSettings.v1", key)`.

**Migration** `AddPlatformSettings`: both tables, in one migration with the declarative twin
(DATABASE.md §3). Two new packages — `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` and
`Azure.Extensions.AspNetCore.DataProtection.Keys` (with `Azure.Identity`), both MIT — go into
`DEPENDENCY_POLICY.md` and `ForbiddenPackagesTests` in the same commit as the code that needs them.

## 4. Resolution and consumption

```csharp
// Platform.Application.Settings.Abstractions
public interface ISettingsProvider
{
    /// <summary>Every definition with its effective value, source, and mask; the console's read.</summary>
    Task<IReadOnlyList<SettingView>> GetAsync(SettingCategory? category, CancellationToken ct);

    /// <summary>The raw effective text for one key — the typed binders' input. Sensitive keys come back decrypted.</summary>
    Task<string?> GetEffectiveAsync(string key, CancellationToken ct);

    /// <summary>Drops this instance's cache. Called after every write; other replicas wait out the TTL.</summary>
    void Invalidate();
}

public interface ISettings<T> where T : class, new()
{
    /// <summary>The current typed value: configuration defaults overlaid by rows, validated. Never null.</summary>
    T Current { get; }
}
```

**`SettingsProvider`** (Platform.Infrastructure.Settings) loads **all rows in one query** into a
per-instance cache (`Settings:CacheTtl`, default 30 seconds), decrypts sensitive rows on load, and
overlays them on `IConfiguration` by each definition's `ConfigurationPath`. Every read in the process is
served from the cache; `Invalidate()` after a write makes the next read reload. On a two-replica
deployment the other replica lags by at most the TTL — bounded staleness, stated, and the outbox event
of AD-45 replaces the TTL when the processor exists.

**`ISettings<T>` binds the merged layers into the existing options classes** — `EmailOptions`,
`LoginPolicyOptions`, `JwtOptions` (lifetime only; the key, issuer and audience still come from
configuration and are never overlaid), `MonitoringOptions` (retention only), and a new
`MaintenanceOptions` and `RateLimitOptions`. The binder is `IConfiguration`'s own: a
`MemoryConfigurationSource` of the overlaid paths chained over the real configuration, bound with
`ConfigurationBinder`, then run through the registered `IValidateOptions<T>`. **If the bound `T` fails
validation at read time** — a row edited by hand, a configuration change under a saved row — the
provider logs at Error, keeps serving the **last valid `T`**, and the console's `GET /settings` shows the
offending key with `valid: false` and the message. A platform whose email settings are broken must keep
signing people in.

**Consumers change type, nothing else.** `IOptions<EmailOptions>` → `ISettings<EmailOptions>` in
`SmtpEmailSender` and `ActivationLinks`; `IOptions<LoginPolicyOptions>` → `ISettings<LoginPolicyOptions>`
in `SignInCommandHandler`, `SessionFactory`, `SetPasswordCommand`, `ChangePasswordCommandHandler`,
`SuperAdminSeeder`; `IOptions<JwtOptions>` → `ISettings<JwtOptions>` in `JwtTokenService` (issuing;
`JwtBearerOptionsPostConfiguration` keeps `IOptions` — validation parameters are startup state);
`IOptions<MonitoringOptions>` → `ISettings<MonitoringOptions>` in `JobRunMaintainer` and the two
monitoring handlers. `.Value` becomes `.Current`. Nine sites, mechanical, each covered by an existing test.

## 5. The write path

`PUT /settings` takes one category and a batch:

```json
{ "category": "Email", "values": [ { "key": "email.smtp_host", "value": "smtp.example.test" },
                                  { "key": "email.smtp_password", "value": "••••" },
                                  { "key": "email.smtp_port", "value": null } ] }
```

In order, all-or-nothing, one transaction:

1. Every key must be defined **and** belong to the named category (`settings.unknown_key`,
   `settings.wrong_category`, 400 naming the key).
2. Every non-null value must parse as the definition's type and pass its range (`settings.invalid_value`,
   400 naming the key and the rule).
3. **The mask is "unchanged".** A sensitive key whose value is exactly `••••` is skipped. An empty string
   clears it, which deletes the row exactly as `null` does, so the check constraint in §3 always holds.
   Anything else replaces it. Non-sensitive keys have no mask semantics.
4. `null` deletes the row — the key returns to the layer below.
5. The would-be `T` for the category is built from the merged layers and run through its validators
   (`settings.invalid_state`, 400 with the validator's message — e.g. "Email:Smtp:Host is required when
   Email:Enabled is true"). This is where enabling email without a host is refused.
6. Rows upsert under the row version; a concurrent edit is 409 (`settings.concurrent_update`).
7. `Invalidate()`.

Response: the category's `SettingView` list, as `GET` would return it. The interceptor audits each row
change with `ProtectedValue` redacted; no extra audit call is needed for `PUT`.

**`POST /settings/test-email`** — the semantic validator for the Email category, and **not a message**.
Reads `ISettings<EmailOptions>.Current`, then with MailKit: connect to host and port with the configured
STARTTLS mode, authenticate with the saved credentials when a username is set, `NoOp`, disconnect, a
15-second overall timeout. `200 { "ok": true, "server": "<EHLO greeting>" }` or `200 { "ok": false,
"stage": "Connect | Tls | Authenticate", "error": "<server text>" }` — a failed test is a successful test
call. Refused with 400 `settings.email_disabled` when `email.enabled` is false. Audited as the non-entity
action `settings.email_tested` with the stage and outcome (AD-7). Nothing here touches `IEmailSender`, so
ADR-53's two messages remain two.

**`POST /settings/maintenance-mode`** — `{ "enabled": true, "message": "…" }`, requiring
`X-Confirm-Action: maintenance` (AD-6; `settings.confirmation_required`, 400, when absent or wrong —
the same `RequiresConfirmAction()` the gym delete and invoice void use). Writes the two Maintenance rows
through the same path as `PUT`, then records `settings.maintenance_mode_changed` with the new state as
a non-entity audit action, so the toggle is findable without reading two row diffs. Separate endpoint
because §3.10 says so and because a batch `PUT` to the Maintenance category should not be able to take
the platform down as a side effect: **`PUT /settings` refuses the Maintenance category** (`settings.use_maintenance_endpoint`).

## 6. Maintenance mode

`MaintenanceModeMiddleware` (Host, `Middleware/`) sits **after `UseAuthentication()` and before
`UseAuthorization()`**: it needs the principal to know who is asking, and it must answer before any
endpoint's own authorization runs. Per request it reads `ISettings<MaintenanceOptions>.Current` (cached)
and:

- passes when `Enabled` is false;
- always passes `/health/live`, `/health/ready`, `/version`, `POST /api/v1/auth/login`,
  `POST /api/v1/auth/refresh` — probes must keep probing and a SuperAdmin must be able to get in;
- passes any request whose principal carries the `SuperAdmin` role (`ICurrentUser`'s role claim, the
  same one the authorization behaviour reads), so the console keeps working for whoever turned the mode
  on and can turn it off;
- otherwise short-circuits with **503**, problem details `type: settings.maintenance`, `title:
  "Maintenance in progress"`, `detail: <maintenance.message>`, and `Retry-After: 300`. Nothing downstream
  runs — no handler, no database read.

The console reads the same two keys from `GET /settings?category=Maintenance` (masked like any other) to
render its banner; `settings.read` is enough.

## 7. Rate limiting

Two policies on the global limiter, replacing the sign-in-only registration in `RateLimitingExtensions`,
every number a Security setting read from `ISettings<RateLimitOptions>.Current` **inside the partition
factory**, so a saved change applies on the next request without a restart:

| Policy | Partition | Limiter | Default |
|---|---|---|---|
| `authenticated` (global) | user id claim | sliding window, 6 segments, 1 minute | `security.api_requests_per_minute` = 600 |
| `anonymous` (global) | remote IP | sliding window, 6 segments, 1 minute | `security.anonymous_requests_per_minute` = 60 |
| `sign-in` (existing, per endpoint) | remote IP | fixed window | `security.sign_in_attempts_per_window` = 10 per `security.sign_in_window_seconds` = 60 |

The global limiter is a `PartitionedRateLimiter.CreateChained` of the two, chosen by whether the
request is authenticated; `/health/*` and `/version` resolve to a `RateLimitPartition.GetNoLimiter`
partition, because a probe that is throttled is an outage that is not one.
Rejections are 429 with `Retry-After` in whole seconds and problem details `type: security.rate_limited`.
**Limits are per replica**, the in-memory limiter's honest scope; a distributed store is deferred (§1) and
the docs say a two-replica UAT allows twice the number.

## 8. Audit export

`GET /api/v1/audit-logs/export?from=&to=&userId=&action=&entityType=&entityId=` — the list's filters,
minus paging, plus:

- `from` and `to` **required**, inclusive, at most **366 days** apart (400, `audit.export_range`) — the
  analytics precedent; an export without a window is a table dump by another name.
- Streams `text/csv; charset=utf-8` with `Content-Disposition: attachment;
  filename="audit-logs-<from>-<to>.csv"`, ordered by `created_at_utc` ascending, through a new
  `IAuditLogQueries.StreamAsync(AuditLogExportFilter, ct)` returning `IAsyncEnumerable<AuditLogListItem>`
  that reads with `AsNoTracking().AsAsyncEnumerable()` — no `ToList`, no page cap, one open reader.
- Columns, in order: `id, created_at_utc, user_email, user_id, ip_address, action, entity_type,
  entity_id, correlation_id`. **No `old_values` / `new_values`**: they are JSON blobs the detail endpoint
  serves and a redacted secret must not tour the platform in a spreadsheet.
- RFC 4180: every field quoted, quotes doubled, CRLF; cells beginning with `=`, `+`, `-`, `@`, tab or CR
  are prefixed with `'` so a spreadsheet does not execute a column (CSV injection).
- Permission **`audit.export`**, new, granted to SuperAdmin and PlatformAdmin, **not SupportAgent** (who
  keeps `audit.read`): a bulk file of who did what, with IP addresses and emails, is personal data under
  GDPR leaving the system, and the person who may read a screen is not automatically the person who may
  walk out with the table.
- Audited (AD-7 names export explicitly) as the non-entity action `audit.exported` with the filter and
  the row count in details, written **after** the stream completes so the count is real.

## 9. Endpoints and permissions

| Method | Route | Permission | Confirm |
|---|---|---|---|
| GET | `/api/v1/settings?category=` | `settings.read` | |
| PUT | `/api/v1/settings` | `settings.manage` | |
| POST | `/api/v1/settings/test-email` | `settings.manage` | |
| POST | `/api/v1/settings/maintenance-mode` | `settings.manage` | `X-Confirm-Action: maintenance` |
| GET | `/api/v1/audit-logs/export` | `audit.export` | |

**`settings.read`**: SuperAdmin (implicit), PlatformAdmin, ReadOnly — masked reads carry no secret.
**`settings.manage`**: **SuperAdmin only.** The category holds SMTP credentials, the lockout policy and
the token lifetime; changing any of them is platform-level authority, the same reasoning that keeps
`billing.manage` from PlatformAdmin. **`audit.export`**: SuperAdmin, PlatformAdmin.

`SettingView` — the read shape for every definition:

```csharp
public sealed record SettingView(
    string Key, SettingCategory Category, SettingValueType ValueType, bool IsSensitive,
    string Description,
    string? Value,            // "••••" for a sensitive key that has a value; null when it has none
    SettingSource Source,     // Database | Configuration | Default
    bool Valid, string? ValidationMessage,
    Guid? UpdatedBy, DateTimeOffset? UpdatedAtUtc);
```

## 10. Out of scope, stated

- Branding, Storage, AI, Payment categories and their providers.
- Tenant or gateway scope; `setting_definitions` / `setting_value_history` tables; JSON Schema; the
  `requires_restart` flag and banner (nothing in this catalog needs a restart).
- Cross-replica invalidation through the outbox; distributed rate limiting.
- Creating the Key Vault and the managed identity — the owner's, recorded in AZURE.md §7 with the
  commands, like min replicas and the free-offer setting before it.
- A test *message*; any change to `IEmailSender` or ADR-53.
- Console pages that read any of this — GymOS-Platform increments; M7 is the API they will read.
- Migrating the console's own environment (`NEXT_PUBLIC_*`) to settings.

## 11. Verification plan

**Unit.** Domain: the catalog — every key matches `{category}.{name}`, categories agree with prefixes,
no duplicate keys, every default parses as its type, every non-empty `ConfigurationPath` exists as a
property on its options class (reflection). `PlatformSetting.Set` refuses a plain value on a sensitive
definition and vice versa. Application: `PUT` handler — unknown key, wrong category, bad type, out of
range, the mask leaves a sensitive row untouched, empty string clears, `null` deletes, an
`EmailOptions` validator failure rejects the whole batch, Maintenance category refused; the CSV writer —
quoting, doubling, CRLF, formula-prefix neutralisation, column order; export range rule. Host:
`MaintenanceModeMiddleware` against a fake principal — off passes, on blocks anonymous with 503 and
`Retry-After`, on passes SuperAdmin, always passes the exempt paths.

**Integration (real SQL Server, the leased-database pattern).** A sensitive value round-trips: `PUT`
stores ciphertext in `protected_value` and null in `value`, `GET` shows `••••`, `ISettings<EmailOptions>.Current.Smtp.Password`
is the plaintext, the audit row for the change has `protected_value` as `***REDACTED***` and
`smtp_host` in plain. Resolution order: no row → configuration → `Source: Configuration`; row → `Database`;
`PUT null` → back to `Configuration`. `Invalidate()` — a write is visible to `Current` on the next read
in the same process. A key-ring row exists in `platform.data_protection_keys` after the first protect, and
a second `AppDbContext` on the same database decrypts what the first encrypted (the scale-to-zero case).
Concurrent `PUT`s to the same key → one 409. `test-email` against a Testcontainers MailHog/`mailpit`
container succeeds, against a closed port reports `stage: Connect` with 200. Maintenance: toggle with
the header → anonymous request 503, SuperAdmin request 200, `/health/ready` 200, sign-in 200, toggle
without the header 400, audit row `settings.maintenance_mode_changed` present. Rate limiting: a
PlatformAdmin with `security.api_requests_per_minute` set to 3 gets 429 with `Retry-After` on the fourth
call within the window, and 200 again after the setting is raised (no restart). Export: 1,200 seeded
rows stream back as 1,201 lines with the header, in ascending order, with a quoted field containing a
comma and a quote intact and a leading `=` neutralised; SupportAgent gets 403; a 400-day range gets 400;
the `audit.exported` row carries `rowCount: 1200`. Permissions: `settings.manage` 403 for PlatformAdmin.

**Live.** Compose stack: set an SMTP password from the console-less API, restart the API container,
confirm the password still decrypts (the ring survived in the database); turn maintenance on, watch a
ReadOnly session get 503 while the SuperAdmin session keeps working, turn it off.
