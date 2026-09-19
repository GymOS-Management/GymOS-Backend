# Settings & Hardening — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (every key here changes the
whole deployment's behaviour, not one gym's)
**Status:** **implemented — M7.** This document records the rules as built.
Design: [`docs/superpowers/specs/2026-09-19-m7-settings-and-hardening-design.md`](../superpowers/specs/2026-09-19-m7-settings-and-hardening-design.md).

## Purpose

Five things M1 through M6 either hardcoded or left open: an administrator could not change SMTP
credentials, lockout thresholds or token lifetimes without a redeploy; there was no way to take the
platform down for maintenance without stopping the process; the sign-in limiter was the only rate
limit in the API; and the audit log — real since M1 — could be read one page at a time but never
exported. M7 ships a declared settings catalog with a typed read path, a confirmed maintenance-mode
toggle, a global API rate limiter tuned from the same catalog, and a streamed CSV export of the audit
trail. All four share one root idea: **the source of truth for what a setting *is* is code; the
source of truth for what it is *set to* is a database row that may not exist.**

## 1. Definitions are code; rows are values

`PlatformSettingDefinitions.All` (`GymOS.Platform.Domain.Settings`) is the catalog, exactly as
`PlatformPermissions.All` is for permissions: a value without a matching `Key` cannot be written
(400, `settings.unknown_key`), and the catalog — not the `platform_settings` table — is what
`GET /settings` enumerates, so every definition appears whether or not a row exists for it.

**24 keys, five categories**, each with a consumer today:

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
| Security | `security.sign_in_window_seconds` | Int | | `60` | `Security:RateLimiting:SignInWindowSeconds` |
| Security | `security.api_requests_per_minute` | Int | | `600` | `Security:RateLimiting:ApiRequestsPerMinute` |
| Security | `security.anonymous_requests_per_minute` | Int | | `60` | `Security:RateLimiting:AnonymousRequestsPerMinute` |
| Maintenance | `maintenance.enabled` | Bool | | `false` | `Maintenance:Enabled` |
| Maintenance | `maintenance.message` | String | | `GymOS is undergoing maintenance…` | `Maintenance:Message` |
| Retention | `retention.job_runs_days` | Int | | `30` | `Monitoring:JobRunRetentionDays` |

**Deviation from the plan's own test.** The plan's verification list said 25 keys and named an
invented `branding.primary_color` that had no consumer and no options class to bind into; it was
removed before review, and the catalog that shipped, in `PlatformSettingDefinitionsTests`, is the
24 above — the same table the spec's §2 names.

**Durations carry their unit in the key** (`security.lockout_minutes`, not an ISO-8601 duration), so
the four value types (`String`, `Int`, `Bool`, `Json`) stay enough. `SettingDefinition.Unit`
(`Minutes`, `Hours`, `Days`, `Seconds`, or `None`) is not part of the wire shape; it only tells
`ToConfigurationValue` how to convert a stored integer into the `TimeSpan` string the target options
property actually binds — "15" for `security.lockout_minutes` overlays
`Security:LoginPolicy:LockoutDuration` as `00:15:00`, not fifteen days. `FromConfigurationValue` is
the inverse, used when a configuration-sourced (not row-sourced) duration is shown back in the key's
unit.

**`security.sign_in_window_seconds` deviates from the spec's own table.** §2's table names the
`ConfigurationPath` `Security:RateLimiting:SignInWindow`, the pre-existing `TimeSpan` property. That
would have made this key a duration overlaying a `TimeSpan` — but every other duration in the
catalog is deliberately an `Int` whose `Unit` converts it, and `SignInWindow` was the one path that
was already a `TimeSpan` rather than an `Int` waiting for a unit. Overlaying an integer seconds value
directly at a `TimeSpan` path would bind as **days**, not seconds (`TimeSpan`'s `IConvertible`
parsing of a bare number), turning a 60-second window into a 60-day one. The key instead overlays a
new `Int` path, `Security:RateLimiting:SignInWindowSeconds`, and `RateLimitOptions.SignInWindowSeconds`
is the property `SlidingWindowRateLimiterOptions`/`FixedWindowRateLimiterOptions` read; the old
`RateLimiting:SignInWindow` path in `appsettings.json` is no longer read by anything. This is the
general rule stated once here: **every duration-shaped key carries a unit and is converted to the
TimeSpan string its options property expects; none is stored or transmitted as a raw TimeSpan.**

**Never settings**, unchanged from the spec: the JWT signing key, issuer and audience, connection
strings, the SuperAdmin seed password, Argon2 parameters, `Jobs:Enabled`, `Monitoring:StaleGrace`.
Each is either a secret whose custody is the deployment's, a value that changes what a token *means*,
or a per-process operational switch — never something an administrator changes from a console.

**Six `appsettings.json` keys were removed, not merely superseded**, so those settings honestly report
`Source: Default` rather than `Source: Configuration` when no row exists: `Jwt:AccessTokenLifetime`,
`LoginPolicy:MaxFailedAttempts`, `LoginPolicy:LockoutDuration`, `LoginPolicy:RefreshTokenLifetime`,
`LoginPolicy:MinimumPasswordLength`, and `RateLimiting:SignInAttemptsPerWindow`. Each is replaced in
the file by a `//`-prefixed comment key pointing at the setting that now owns the default. Leaving the
old value in place would have made `SettingSource.Configuration` a lie: the code default in
`PlatformSettingDefinitions`, not the JSON, would be the one actually enforced when no row exists,
and the two could silently drift.

**Validation lives with the definition and with the options class.** Each definition's `ValidateValue`
checks the type and, for `Int`, a `Min`/`Max` range (`email.smtp_port` 1–65535, lifetimes and limits
≥ 1). Because the consumers are the *existing* options classes with their *existing*
`IValidateOptions<T>` chains (`EmailOptions` demands a host and a from-address when enabled;
`LoginPolicyOptions` and the rest carry data annotations), a `PUT` additionally builds the would-be
`T` from the merged layers and runs that chain before writing anything — §4 below.

## 2. Resolution order and sources

A read resolves each definition, independently, as **row → configuration path → code default**, and
reports which layer answered as `SettingSource`: `Database`, `Configuration`, or `Default`.
`platform.platform_settings` holds **rows only for values someone has set** — there is no row for a
key nobody has touched. Deleting a row (`PUT` with `null` or `""`) returns the key to the layer below
it; a console can therefore show "reset to default" honestly, and a deploy with zero rows behaves
exactly as it did before M7. This is `SettingsProvider.LoadAsync` — one query for every row, matched
against the whole catalog, in that order, every time the cache reloads (§5).

## 3. Secrecy

`platform.platform_settings` has **two value columns**, not the spec's original one:

| column | notes |
|---|---|
| `value` | plain text; **null for sensitive keys** |
| `protected_value` | Data Protection ciphertext; **null for non-sensitive keys** |

**Why two columns.** The audit interceptor redacts by *property name*
(`AuditPolicy.ShouldRedact`), not by row, so a single `value` column would mean either every setting
change is redacted from the audit log or ciphertext lands in it in plain sight. Two columns give the
interceptor a name to redact always (`ProtectedValue`, registered `alwaysRedacted` on
`AuditedEntity.Of<PlatformSetting>("platform_setting", …)`) and a name to record in plain (`Value`),
so "who changed the SMTP host from what to what" is answerable from the audit trail and the password
never is. Exactly one of the two is populated, per the check constraint:

```sql
CONSTRAINT [ck_platform_settings_one_value] CHECK (
    ([value] IS NULL AND [protected_value] IS NOT NULL) OR
    ([value] IS NOT NULL AND [protected_value] IS NULL))
```

**This is `<>` rewritten, not `<>` itself.** The spec's §3 wrote the constraint with `<>`
("exactly one of the two is null"), which is not valid T-SQL for an equivalence between two
nullable comparisons; the shipped predicate is the pair of `AND`/`OR`-connected `IS [NOT] NULL`
clauses above, semantically the same rule, syntactically what SQL Server accepts.

**Data Protection, key ring in the database.** Sensitive values are protected with ASP.NET Data
Protection; `PlatformSetting`'s protector is created per row from the key with a fixed purpose
string, `CreateProtector("GymOS.PlatformSettings.v1", key)`, so a ciphertext moved to another row's
`protected_value` cannot be decrypted there. The key ring itself —
`Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`'s `DataProtectionKey` table, mapped as
`platform.data_protection_keys` through `AppDbContext implementing IDataProtectionKeyContext` — lives
in the database, not on disk, because UAT scales to zero and a replica that comes back with a fresh
file system must still decrypt yesterday's row.

**The gap: until Key Vault exists, the ring is unprotected beside the ciphertext it decrypts.** A full
database dump exposes every sensitive setting; this is written as the standing gap in
[`AZURE.md` §7](../Infrastructure/AZURE.md), item 6, with the commands that close it. The ciphertext's
on-disk format does not change when that happens — `ProtectKeysWithAzureKeyVault` re-wraps the keys in
the ring, not the payloads already encrypted with them.

**A row whose populated column disagrees with the catalog's `IsSensitive` is not a crash.**
`SettingsProvider.LoadAsync` guards this explicitly: a sensitive definition whose row has `value` set
and `protected_value` null satisfies the check constraint (it does not know about `IsSensitive`) but
would throw `ArgumentNullException` inside `Unprotect`. The provider catches this case (and a genuine
`CryptographicException` from a ring that can no longer decrypt an existing ciphertext) by logging at
Error and treating the row as if it did not exist — the definition falls through to configuration or
its default rather than taking the whole settings reload down.

## 4. The write path

`PUT /settings` takes one category and a batch of `{ key, value }` pairs, and applies it **all-or-nothing,
in one transaction**, in this order (`SettingsWriter.ApplyAsync`):

0. **No key may appear twice in one batch** (`settings.duplicate_key`, 400). This check runs before
   anything else and exists because `ISettingsRepository.FindByKeyAsync` queries the database, not
   the EF change tracker: a second occurrence of the same key in one call would look up the same
   (still-unmodified) row twice and either insert a duplicate or silently apply only the last write,
   rather than failing cleanly.
1. Every key must be defined **and** belong to the named category (`settings.unknown_key`,
   `settings.wrong_category`, 400 naming the key).
2. Every non-empty, non-mask value must parse as the definition's type and pass its range
   (`settings.invalid_value`, 400 naming the key and the rule).
3. **The mask is "unchanged".** A sensitive key whose value is exactly `••••`
   (`PlatformSettingDefinitions.Mask`) is skipped entirely — no read, no write, no validation against
   it. An empty string clears it, which deletes the row exactly as `null` does, so the check
   constraint in §3 always holds. Non-sensitive keys have no mask semantics; any string is a real
   value including one that happens to equal `••••`.
4. `null` or `""` deletes the row — the key returns to the layer below.
5. The would-be merged overlay (current database rows with this batch's non-skipped values applied)
   is bound into the category's options class(es) and run through their `IValidateOptions<T>` chains
   (`OptionsSettingsValidator`, via the same `SettingsBinder<T>.Bind` the running application reads
   from) — `settings.invalid_state`, 400, with the validator's own message. This is where enabling
   email without a host is refused, and it is checked **before** any row is touched.
6. Rows upsert under the row-version token; a concurrent edit surfaces as the existing
   `ConcurrencyConflictException` → 409 handler.
7. `_provider.Invalidate()`.

The response is the category's fresh `SettingView` list, exactly as `GET` would return it. The audit
interceptor records each row change with `ProtectedValue` redacted and `Value` in plain; no extra
audit call is needed for `PUT` itself.

## 5. Typed consumption and live reload

```csharp
public interface ISettingsProvider
{
    Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct);
    IReadOnlyDictionary<string, string?> GetOverlayNow();
    void Invalidate();
}

public interface ISettings<T> where T : class, new()
{
    T Current { get; }
}
```

`SettingsProvider` is a per-process singleton cache, TTL `Settings:CacheTtl` (default 30 seconds).
`ISettings<T>` (`SettingsBinder<T>`) is **also a singleton**, guarded by a `Lock`: it binds the
provider's overlay into the existing options class through `IConfiguration`'s own `ConfigurationBuilder`
(rows chained over real configuration, "later wins") and runs the registered `IValidateOptions<T>`
chain. **On an invalid overlay it serves the last valid `T` it ever bound**; if none exists yet (the
very first read, or every read so far has failed), it falls back to binding with no overlay at all —
configuration plus code defaults, exactly the state `ValidateOnStart` already proved safe at boot —
and only throws `OptionsValidationException` if even that fails. A platform whose email settings are
broken by a bad row must still be able to sign people in.

**Stale-while-revalidate, not blocking, for synchronous readers.** `Invalidate()` (called after every
write) only **marks** the cached snapshot stale; it does not drop it. `GetOverlayNow()` — the
synchronous call `SettingsBinder<T>.Current` uses, because option binding cannot `await` — returns the
existing snapshot immediately when one exists, kicking off at most one background refresh
(`Interlocked.CompareExchange`-guarded) if it is stale or past its TTL. The **only** case
`GetOverlayNow()` blocks is a cold process with no snapshot at all yet — the same cost every other
first reader already pays after start-up. The async readers (`GetEffectiveAsync`, `GetOverlayAsync`,
used by the read/write handlers) always await a fresh load when the snapshot is stale, which is what
lets a `PUT`'s own re-read see the row it just wrote in the same request.

**Consumers changed type, nothing else** — `IOptions<T>` → `ISettings<T>`, `.Value` → `.Current` — in
`SmtpEmailSender`, `ActivationLinks` (`EmailOptions`); `SignInCommandHandler`, `SessionFactory`,
`SetPasswordCommand`, `ChangePasswordCommandHandler`, `SuperAdminSeeder` (`LoginPolicyOptions`);
`JwtTokenService` issuing only (`JwtOptions`; `JwtBearerOptionsPostConfiguration` keeps `IOptions` —
validation parameters are startup state); `JobRunMaintainer` and the two monitoring handlers
(`MonitoringOptions`, retention only). `MaintenanceOptions` and `RateLimitOptions` are new.

## 6. Test-email is a connection test (ADR-53)

`POST /settings/test-email` reads `ISettings<EmailOptions>.Current` and, with MailKit, connects to
the configured host and port with the configured STARTTLS mode, authenticates when a username is set,
issues `NoOp`, disconnects — a 15-second overall timeout. It never resolves `IEmailSender` and never
composes a message, so ADR-53's two-message limit is untouched. `200 { ok: true, stage: null, server:
"<capabilities>", error: null }` on success; `200 { ok: false, stage: "Connect" | "Tls" |
"Authenticate", error: "<message>" }` on a failure the caller should see — a failed test is still a
successful test *call*. Refused with 400 `settings.email_disabled` when `email.enabled` is false.

**Two implementation facts worth recording.** `ISmtpConnectionTester.IsEmailEnabled` exists as its own
member, separate from `TestAsync`, because Application must not depend on Infrastructure (rule R7) and
so cannot see `EmailOptions` itself to check `.Enabled` before deciding whether to call `TestAsync` at
all — the interface exposes exactly the one fact the command handler needs across that boundary.
And `MailKitSmtpConnectionTester` catches `SmtpCommandException` explicitly alongside
`MailKit.ProtocolException`, `SocketException`, `IOException` and `AuthenticationException`: verified
against MailKit 4.17 (the version pinned in `Directory.Packages.props`), `SmtpCommandException`
derives from `MailKit.CommandException`, not `MailKit.ProtocolException`, so folding it into the
latter would have let a server's rejection of `NoOp` or `Authenticate` escape uncaught. `server` in
the result is not a greeting banner — MailKit does not expose one past `ConnectAsync` — it is the
server's advertised capability list (`client.Capabilities.ToString()`); an honest value, oddly named.

## 7. Maintenance mode

`MaintenanceModeMiddleware` (Host, `Middleware/`) sits **after `UseAuthentication()` and before
`UseAuthorization()`** in `Program.cs`: authentication has populated `HttpContext.User` by the time it
runs, and no route's own authorization has executed yet, so a refused caller never reaches a handler.
Per request it reads `ISettings<MaintenanceOptions>.Current` and:

- passes when `Enabled` is false;
- always passes `/health*`, `/version`, `POST /api/v1/auth/login`, `POST /api/v1/auth/refresh`;
- passes any caller who currently holds **`settings.manage`** (checked live through
  `IPermissionEvaluator`, scoped and DB-backed with a per-request cache);
- otherwise short-circuits with **503**, problem details `type: "settings.maintenance"`, `title:
  "Maintenance in progress"`, `detail: <maintenance.message>`, and `Retry-After: 300`.

**"SuperAdmin passes" is implemented as "holds `settings.manage`," not as a role check**, because
`ICurrentUser`/the JWT carries no role claim at all — permissions are the only thing a token asserts.
`settings.manage` is granted to SuperAdmin alone (§9), so the two are equivalent in practice; the
distinction matters only if a future role were ever granted `settings.manage` without being
`SuperAdmin`, at which point that role would also pass maintenance mode, by design of the permission
rather than as a special case for a role name. The console reads the same two keys from
`GET /settings?category=Maintenance` (masked like any other) to render its banner; `settings.read` is
enough for that.

`POST /settings/maintenance-mode` requires the header `X-Confirm-Action: maintenance`
(`settings.confirmation_required` when absent, `settings.confirmation_mismatch` when present but
wrong — AD-6, the same mechanism the gym-delete and invoice-void endpoints use), writes the two
Maintenance rows through the same `SettingsWriter` path as `PUT`, and separately records
`settings.maintenance_mode_changed` as a non-entity audit action with the new state. **`PUT /settings`
refuses the Maintenance category outright** (`settings.use_maintenance_endpoint`, 400) — a batch write
to any other category cannot take the platform down as a side effect; only the dedicated, confirmed
endpoint can.

## 8. Rate limiting

Two named policies replace the sign-in-only registration `RateLimitingExtensions` used to hold, plus
a new global limiter, all read from `ISettings<RateLimitOptions>.Current` **inside the partition
factory** on every request — a saved change applies to the next request with no restart:

| Policy | Partition | Limiter | Default |
|---|---|---|---|
| Global, authenticated | user id claim | sliding window, 6 segments, 1 minute | `security.api_requests_per_minute` = 600 |
| Global, anonymous | remote IP | sliding window, 6 segments, 1 minute | `security.anonymous_requests_per_minute` = 60 |
| `sign-in` (existing, per endpoint) | remote IP | fixed window | `security.sign_in_attempts_per_window` = 10 per `security.sign_in_window_seconds` = 60 |

The global limiter is one `PartitionedRateLimiter<HttpContext, string>.Create`, keyed by whether the
request carries a subject claim; `/health*` and `/version` resolve to `RateLimitPartition.GetNoLimiter`
so a probe is never throttled. A rejection is **429** with problem details `type:
"security.rate_limited"` and `Retry-After` in whole seconds.

**`SlidingWindowRateLimiter` — the global limiter's kind — never reports `Retry-After` metadata**
(verified against this SDK's `System.Threading.RateLimiting`; `FixedWindowRateLimiter`, which the
sign-in policy uses, always does). `OnRejected` falls back to a fixed 10 seconds
(`GlobalLimiterFallbackRetryAfter`) when the lease carries none — one segment of the global limiter's
own six-segment, one-minute window, the shortest interval guaranteed to have freed some capacity.

`UseRateLimiter()` moved to **after `UseAuthentication()`**, so the global limiter's partition factory
can read the subject claim; the final middleware order in `Program.cs` is: `UseAuthentication` →
`UseRateLimiter` → `MaintenanceModeMiddleware` → `UseAuthorization`.

**Limits are per replica** — the in-memory limiter's honest scope. A distributed store is deferred
(design §1); a two-replica deployment allows roughly twice the configured number in aggregate.

## 9. Audit export

`GET /api/v1/audit-logs/export?from=&to=&userId=&action=&entityType=&entityId=` — the list's filters
minus paging:

- `from` and `to` are **required**, inclusive, **at most 366 days apart** (`audit.export_range`, 400).
- Streams `text/csv; charset=utf-8` via `Results.Stream`, `Content-Disposition: attachment;
  filename="audit-logs-<from>-<to>.csv"`, through `IAuditLogQueries.StreamAsync` returning
  `IAsyncEnumerable<AuditLogListItem>` (`AsNoTracking().AsAsyncEnumerable()` — no `ToList`, one open
  reader), ordered by `created_at_utc` ascending.
- **Columns, fixed order:** `id, created_at_utc, user_email, user_id, ip_address, action, entity_type,
  entity_id, correlation_id`. No `old_values`/`new_values` — those are read per entry through
  `GET /audit-logs/{id}` instead of touring the platform's redacted secrets in a spreadsheet.
- **Escaping (`AuditCsv`):** every field quoted, an embedded quote doubled, and a field whose first
  character is `=`, `+`, `-`, `@`, tab, or carriage return is prefixed with `'` **before** quoting, so
  the guard character survives the very quote a spreadsheet strips on open (CSV-injection defence).
  Rows carry **exactly one CRLF** — the `StreamWriter`'s own `NewLine = "\r\n"`; `AuditCsv.FormatRow`
  itself writes no line terminator, so there is never a doubled one.
- **Permission `audit.export`**, new: granted to **SuperAdmin and PlatformAdmin, not SupportAgent**
  (who keeps `audit.read`) — a bulk file of who did what, with IP addresses and emails, is personal
  data under GDPR leaving the system, and the ability to read a screen is not the ability to walk out
  with the table.
- Audited as the non-entity action `audit.exported`, with the filter and the row count, recorded
  **after** the stream completes (from inside the `Results.Stream` callback, in the same request
  scope) so the count reflects lines that actually reached the response body.
- The OpenAPI response declares `text/csv` via `.Produces<string>(StatusCodes.Status200OK,
  contentType: "text/csv")` — the handler's real return type is `IResult` (`Results.Stream`), and this
  annotation exists purely to document the content type Scalar/clients should expect.

## 10. Endpoints

| Method | Route | Permission | Confirm | Error codes |
|---|---|---|---|---|
| GET | `/api/v1/settings?category=` | `settings.read` | | `settings.unknown_category` |
| PUT | `/api/v1/settings` | `settings.manage` | | `settings.unknown_key`, `settings.wrong_category`, `settings.invalid_value`, `settings.duplicate_key`, `settings.unknown_category`, `settings.invalid_state`, `settings.use_maintenance_endpoint`, 409 |
| POST | `/api/v1/settings/test-email` | `settings.manage` | | `settings.email_disabled` |
| POST | `/api/v1/settings/maintenance-mode` | `settings.manage` | `X-Confirm-Action: maintenance` | `settings.confirmation_required`, `settings.confirmation_mismatch` |
| GET | `/api/v1/audit-logs/export` | `audit.export` | | `audit.export_range` |

`settings.maintenance` (503) is not a write-time error — it is the maintenance middleware's own
short-circuit, on every route it does not exempt. `security.rate_limited` (429) is likewise not
per-endpoint — it is the global limiter's and the sign-in policy's shared rejection shape.

**`GET ?category=` and the PUT body's `category` are parsed the same way and case-insensitively**:
`Enum.TryParse(value, ignoreCase: true, out category) && Enum.IsDefined(category)` — the
`Enum.IsDefined` half matters because `Enum.TryParse` alone accepts a defined enum's *numeric* string
("2") as readily as its name, and the category parser combines both checks so an undefined name or
number alike is `settings.unknown_category`, 400.

## 11. Permissions and their reasoning

- **`settings.read`** — SuperAdmin (implicit, `GrantsAllPermissions`), **PlatformAdmin**, **ReadOnly**.
  Masked reads carry no secret, so this is a wide grant deliberately.
- **`settings.manage`** — **SuperAdmin only.** The category holds SMTP credentials, the lockout policy
  and token lifetimes; changing any of them is platform-level authority, the same reasoning that keeps
  `billing.manage` from PlatformAdmin.
- **`audit.export`** — **SuperAdmin, PlatformAdmin.** Deliberately withheld from SupportAgent (§9):
  reading one entry and walking out with the whole table are different authorities under GDPR.

## 12. Deferred, deliberately

| Deferred | Waits on |
|---|---|
| Tenant and gateway scopes, `setting_definitions` as a table, JSON Schema | AD-45's layered service, when a second scope exists |
| Branding, Storage, AI, Payment categories | Each arrives with the provider it configures |
| Key Vault protection of the Data Protection ring | The owner's action — AZURE.md §7, item 6 |
| A real test *message* from `/settings/test-email` | Would be ADR-53's third message |
| `requires_restart` flag and a console banner for it | Nothing in this catalog needs a restart |
| Cross-replica cache invalidation (outbox-driven) | The outbox processor; M7 uses a 30-second TTL |
| Distributed (cross-replica) rate limiting | Redis or a shared store, when the platform runs more than one replica for real |
| `setting_value_history` | The audit log already answers who/when/what-before for every row |
| Console pages that read any of this | A GymOS-Platform increment; M7 is the API they will read |

## 13. Known limitations, recorded rather than hidden

**TTL staleness is real, in two shapes.** Across replicas, a write on one replica is invisible to
another for up to `Settings:CacheTtl` (30 seconds) — there is no cross-replica invalidation signal
(§12). On the *same* replica, a synchronous reader (`ISettings<T>.Current`, via `GetOverlayNow()`)
can still see the pre-write value for a moment after a successful `PUT` on that same process: the
write path's own re-read is async and sees the fresh row immediately, but a concurrent synchronous
caller reading between the `Invalidate()` call and the background refresh landing gets the
stale-but-not-yet-refreshed snapshot, by design (§5) — correctness for `ISettings<T>.Current` is
"eventually reflects the write, never blocks, never throws," not "reflects the write immediately."

**A wedged background refresh suppresses further attempts, silently, until it resolves.**
`TriggerBackgroundRefresh`'s `Interlocked.CompareExchange` guard means only one refresh runs at a
time; if that refresh hangs (a database that accepts a connection but never answers, for instance) no
further refresh is attempted until it completes or throws, and every synchronous reader keeps serving
the stale snapshot it already had. This is the same trade-off `GetOverlayNow()` makes deliberately —
serving stale data is preferable to a blocked request thread — carried to its edge case.

**Rate-limit partitions keep the old number until their own window rolls over.** A saved change to
`security.api_requests_per_minute` (or the sign-in numbers) applies immediately to a *new* partition
(a user or IP not currently tracked) but an existing partition's in-flight window finishes out under
the limit that was in effect when the window opened — at most one minute's delay for the global
policies, one `security.sign_in_window_seconds` for the sign-in policy.

**Limits are per replica.** The in-memory limiter has no way to see another instance's counters; a
two-replica deployment effectively allows close to twice the configured number.

**The audit export has no row cap beyond the 366-day window**, and does not guard a CR or LF
*embedded inside* a field's own content (as opposed to the leading-character formula guard, which is
a different threat model) — a value containing a literal CRLF still produces a row boundary a naive
CSV parser could misread, though RFC 4180's quoting keeps a *compliant* parser correct. Neither is
exploitable through this system's own inputs today (no field here accepts free-form multi-line text),
but both are stated rather than assumed away.

**The Data Protection key ring sits unprotected beside the ciphertext it decrypts** until the owner
creates the Key Vault (§3, AZURE.md §7 item 6) — a full database dump today exposes every sensitive
setting.

**`TryParseCategory` admits a numeric string for a defined category** (`Enum.TryParse` with
`ignoreCase: true` parses `"2"` to `Security` exactly as it parses `"Security"`), exactly the same
shape as M6's `status` parameter on `/jobs/{name}/runs` — harmless, undocumented in the OpenAPI
surface, and consistent with the precedent rather than fixed ad hoc here.
