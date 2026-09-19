# H1 — Hardening: gym-scoped grants (steps 1–2) and the unblocked review findings — Design

**Status: approved 2026-09-19.** Two decisions taken with the owner: H1's scope is **security first,
plus every review finding that is unblocked** — the first two build steps of the approved
gym-scoped-grants spec, and the findings from the M4.1/M5 close-outs and later whole-branch reviews
that wait on nothing but an increment to carry them; and churn is dated **by when the expiry was
recorded, counted on that day** — which, this spec found, the code has done since `7672bb6`
(2026-08-14): `GymSubscription.ExpiredAtUtc` exists, `RenewalJob` stamps it, `DailyStatsCompiler`
keys `expired_gyms` on it, and `ix_gym_subscriptions_expired_at_utc` and
`ix_gym_subscriptions_canceled_at_utc` both exist. STATUS.md and `docs/BusinessRules/Analytics.md`
§11 still describe all of that as open. H1 corrects the documents; it changes no schema for churn.

**Frozen scope.** `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` §3 steps 1 and 2
(decisions D1–D4) with §4 tests 1–5; the findings enumerated in §3 below, each traced to the STATUS.md
paragraph that recorded it. Steps 3–5 of the grants spec (per-gym roles, `SetGymUserRolesCommand`) belong
to Members and are not here. Nothing in H1 adds an endpoint, a permission, or a setting.

## 1. What H1 delivers, and what it leaves

| Delivered now | Left, and to what |
|---|---|
| `TenantResolutionMiddleware` between authentication and authorization; anonymous gym route → 401 | Steps 3–5 of the grants spec — Members |
| Tenant-aware permission evaluation: platform grants ∪ grants naming the acting tenant; snapshot keyed (user, tenant) | A tenant dimension on `perm_hash` — a cache question, not a correctness one (grants spec §5) |
| `SuperAdminSeeder` under the shared seeding lock; the lock's doc says what it guarantees | `FeatureExceptionMaintainer`'s unlocked plan read — blocked on the call-site audit STATUS names |
| One shared source for the dashboard's living-gyms and billing-contribution queries; one round trip for the four gym counts; ordered `mrr`; name-based `Mrr` navigation config | `GET /features` correlated subqueries — pinned correct by `FeatureAdoptionParityTests`, deferred by design |
| Positive controls (PlatformAdmin, BillingAdmin, ReadOnly) in the two `AnalyticsRead` boundary tests; the null-features fake fails loudly; the `features.read` gate exercised on the six gym write routes; a JPY fixture that can fail; a `DeleteGymCommandHandler` maintainer test | The plan-edit race test's 1-in-8 detection — needs an interleaving hook, its own piece of work |
| `AssignSubscriptionCommandHandler` gains a transaction and reads the plan `FOR UPDATE` inside it | Value-vs-intent for coinciding deny-exceptions — owner decision |
| `DiscardTrackedChanges` banned outside its one legitimate call site | — |
| API-reference launcher: injectable container check, honest `Process.Start` null, honest doc | — |
| Small items: withdrawn-count logging, `StoredException` suppression style, `ActivityEntry.EntityType` null, dev overlay ports on loopback | Dependency majors (BannedApiAnalyzers, Test.Sdk, xunit runner) — a separate chore |
| Docs: STATUS churn rows and the stale reset-password row marked resolved; Analytics.md §11 rewritten around the shipped column; collation difference; `IdempotencyRecordConfiguration` comment; DOCKER.md Mailpit; `IDashboardHealthCheck` "one place"; `/dashboard/health` gating trade-off | Every owner item STATUS lists (Key Vault, `email.smtp_username` sensitivity, ADR-54, e2e CI token, `EnableRetryOnFailure`, clustered GUID PKs, …) |

## 2. Part A — gym-scoped grants, steps 1 and 2

### 2.1 Where the middleware runs (D1, D2)

`Program.cs` today: `UseAuthentication` → `UseRateLimiter` → `MaintenanceModeMiddleware` →
`UseAuthorization` → `TenantResolutionMiddleware`. H1 moves the tenant middleware to immediately after
`UseRateLimiter` and before `MaintenanceModeMiddleware`:

```
UseAuthentication → UseRateLimiter → TenantResolutionMiddleware → MaintenanceModeMiddleware → UseAuthorization
```

Before the maintenance middleware because that middleware asks `IPermissionEvaluator` whether the caller
holds `settings.manage`, and after H1 the evaluator's answer depends on the tenant being set. Before
authorization because that is the whole point: a route policy such as `RequireAuthorization("gyms.read")`
must evaluate against the caller's permissions *for this gym*.

**Anonymous callers.** The middleware's first check after finding a `gymId` route value becomes: if
`ICurrentUser.IsAuthenticated` is false, do nothing and call `_next`. Authorization then challenges as
it does for every other anonymous request — 401 with `WWW-Authenticate: Bearer`. Today that 401 happens
by accident of order: authorization runs first and the middleware never sees an anonymous request.
Moved in front of authorization without this early return, the middleware would answer 404 for every
anonymous gym route (its grant check returns false for a null user) — the hole D2 names. The existing
404 for an authenticated caller with no grant is unchanged (grants spec D2's reasoning stands: 404 hides
the gym's existence from callers who hold nothing on it).

**The route-values claim (grants spec §6).** D1 rests on `WebApplication` inserting `UseRouting` at the
head of the pipeline when endpoints are mapped, so `Request.RouteValues["gymId"]` is populated by the
time this middleware runs. That is framework behaviour the spec took from documentation. H1 proves it
with the test that already exists for the chain (`ARouteWithAGymIdSetsThatTenantForTheRequest`, test 4
below): a signed-in SuperAdmin hits `GET /api/v1/gyms/{gymId}` and the `ITenantContext` observer
registered by `TenantResolutionTests`'s startup filter must report that tenant — run against the
reordered pipeline, that is the proof. If the claim is false the test fails
against the reordered pipeline and the fix is an explicit `app.UseRouting()` before `UseAuthentication`
— a one-line change the plan carries as the contingency, not as the default.

**The middleware's own grant check stays a raw query.** `CallerHoldsAGrantForAsync` reads `UserRole`
directly and keeps doing so. It must not become a permission check: after H1 the permission evaluator
reads the tenant this middleware sets, and a check that depends on its own output is a cycle.

### 2.2 Tenant-aware evaluation (D3)

`IIdentityQueries` today exposes `GetSecuritySnapshotAsync(userId)` — one call that reads the user's
stamp and status and then the user-only permission join — and `GetEffectivePermissionsAsync(userId)`.
Two readers depend on it: `CallerSnapshot` (for `PermissionEvaluator`) and the bearer handler's
security-stamp check in `AuthenticationExtensions.ValidateSecurityStampAsync`, which runs **during
authentication, before any tenant is known**, and needs only the stamp and status.

H1 splits the two concerns so neither reader pays for the other:

```csharp
public sealed record UserSecurityIdentity(Guid UserId, Guid SecurityStamp, string Status);

public sealed record UserSecuritySnapshot(
    Guid UserId, Guid? TenantId, Guid SecurityStamp, string Status, IReadOnlyCollection<string> Permissions);

public interface IIdentityQueries
{
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// Stamp and status only — what the bearer handler needs on every request.
    Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken);

    /// Platform grants only: describes the user, not a request. Kept for SessionFactory,
    /// GetCurrentUserQueryHandler and UserQueries.
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);

    /// Platform grants ∪ grants naming <paramref name="tenantId"/>. With null, exactly the platform grants.
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}
```

`GetSecuritySnapshotAsync` is removed from the interface; `CallerSnapshot` was its only caller. The
tenant-aware predicate is
`assignment.UserId == userId && (assignment.TenantId == null || assignment.TenantId == tenantId)`.
With `tenantId` null the second disjunct is `TenantId == null`, so the result is exactly the platform
grants — today's behaviour on every platform route, by construction rather than by a special case. The
single-argument form delegates with `tenantId: null`.

### 2.3 The snapshot caches identity and permissions separately (D4)

`CallerSnapshot` is scoped per request. Today it caches one `UserSecuritySnapshot` keyed on `userId`.
After H1:

```csharp
public interface ICallerSnapshot
{
    /// Used by the bearer handler's stamp check. Cached per user for the request.
    Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken);

    /// Used by PermissionEvaluator. Identity from the same cache; permissions cached per (user, tenant).
    Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}
```

The stamp check calls `GetIdentityAsync` (one query). Authorization later calls `GetAsync(userId,
tenantContext.TenantId)`: identity comes from the cache, permissions cost one query for that (user,
tenant) pair and are cached under it. A platform route therefore costs the same two queries it costs
today; a gym route costs the same two, one of them tenant-aware. A second `GetAsync` for a different
tenant in the same request recomputes — a safety net that stops a set computed for gym A ever
answering for gym B if the pipeline order is changed again.

`PermissionEvaluator` takes `ITenantContext` alongside `ICurrentUser` and passes `TenantId` through.
`ITenantContext` is already registered per scope; no new registration.

**The unchanged invariant.** `PermissionEvaluator` still returns `[]` for an unauthenticated caller and
for any status other than `Active`. The suspension rule is not tenant-aware and must not become so.

**Fakes.** `FakeIdentityQueries` (Application unit tests) gains `Identities` and a
`(userId, tenantId)`-keyed `TenantPermissions` dictionary beside the existing `Permissions`; every
existing test that populated `Snapshots` is rewritten against `Identities` + `Permissions`. The plan
lists them by `grep`.

### 2.4 Tests (grants spec §4, tests 1–5)

No API can write a tenant-scoped grant yet — `SetUserRolesCommandHandler` refuses non-platform roles
and writes `TenantId = null`. `TenantResolutionTests` already writes the `UserRole` row through the
`DbContext` for exactly this reason, with a documented justification; H1's tests do the same, in the
same file's pattern: create a user via the API, then replace its platform grants with
`user.AssignRole(supportAgentRoleId, gymId)` and save.

| # | Test | Passes today? | What it catches |
|---|---|---|---|
| 1 | A user whose only grant is SupportAgent on gym A gets **403** from `GET /api/v1/audit-logs` (platform route, `audit.read`) | **No** — today the user-only join grants `audit.read` everywhere | F1, the escalation itself |
| 2 | The same user gets **200** from `GET /api/v1/gyms/{A}` (`gyms.read`, which SupportAgent holds) | Yes, *through the escalation*; red if D3 were built as platform-grants-only | D3's positive half; the negative control for test 1 |
| 3 | The same user gets **404** from `GET /api/v1/gyms/{B}` | Yes today (the middleware's own check), and must stay so | The tenant is compared, not merely present |
| 4 | The existing `ARouteWithAGymIdSetsThatTenantForTheRequest` (tenant A observed on `ITenantContext` for a real route) still passes after the move — that is the §6 route-values proof; and test 2 is the ordering proof, because its policy can only be satisfied if the tenant was set before authorization | Observer half: yes; ordering half: no | D1; fails if the middleware is moved back after authorization |
| 5 | An anonymous `GET /api/v1/gyms/{A}` is **401** with `WWW-Authenticate: Bearer`, not 404 | Yes, by accident of order; **red after the reorder** until the early return lands | D2 |

Test 1 is shown failing on the unfixed code; test 5 is shown failing after the reorder and before the
early return; test 2 is the control that would go red under a wrong D3 (the plan orders each). Test 2 is not optional: a tenant-aware evaluator that returns nothing at all would pass
test 1 and be useless.

`ApiEndpointTests`'s secured-route tripwire (65) is untouched — H1 adds no route. The OpenAPI snapshot
does not change.

## 3. Part B — the unblocked findings

Each item names the STATUS.md paragraph that recorded it (line numbers as of the 4,049-line file) and
the change.

### 3.1 Seeding (STATUS 3628, 3661–3663)

- **`SuperAdminSeeder` check-then-act.** The seeder gains `IAdvisoryLock` and follows
  `PlatformIdentitySeeder`'s shape: begin a transaction on the `DbContext`, `AcquireAsync(SeedingLocks.CatalogSeed)`,
  then the exists-check, the insert, `SaveChangesAsync`, commit. Two replicas booting against the same
  empty database now serialise on the lock; the loser sees the winner's row and logs
  `LogAlreadyExists`. Verified by `SuperAdminSeedingTests` gaining a test that runs two seeders
  concurrently against one database and asserts one user.
- **`SeedingLocks` doc.** The comment claims one lock "covering the whole startup-sync window". An
  xact-scoped `sp_getapplock` is held for the transaction that acquired it — each seeder's own — not
  across the seeders. Rewrite: the lock serialises *each seeder's transaction* against the same-named
  transaction in another replica; two different seeders never contend; the guarantee is per-seeder
  atomicity of check-and-insert, which is what each seeder needs.

### 3.2 Dashboard and stats queries (STATUS 3565–3567, 3606–3609)

- **Shared queries.** A new `internal static class SubscriptionAnalyticsQueries` in
  `GymOS.Platform.Infrastructure/Analytics` exposes `LivingGyms(AppDbContext)` → `IQueryable<Gym>` and
  `BillingContributionsAsync(AppDbContext, ct)` → `IReadOnlyList<MrrContribution>`. `DashboardQueries`
  and `DailyStatsCompiler` both call them; the two verbatim copies go.
- **One round trip for four counts.** `GetSummaryAsync` replaces its four `CountAsync` calls with the
  `GroupBy(gym => gym.Status)` → dictionary shape `DailyStatsCompiler` already uses (total = sum of the
  living statuses). Six round trips become three: population, live-subscription count, contributions.
  The contributions materialisation stays — MRR is computed in the domain, deliberately (M5), and the
  row count is the number of paying gyms, which is the platform's own size.
- **`mrr` order.** `MonthlyRecurringRevenue.Calculate` already returns one `Money` per currency; the
  projections in `DashboardQueries` and the revenue series add `OrderBy(m => m.Currency.Code, StringComparer.Ordinal)`
  at the single place each builds its DTO list, so `/dashboard/summary` and `/analytics/revenue` agree.
  A unit test on the summary handler seeds EUR and CHF in the wrong order and asserts the DTO order.
- **`Mrr` navigation.** `DailyPlatformStatsConfiguration` replaces
  `builder.Metadata.FindNavigation(nameof(...))!.SetPropertyAccessMode(...)` with
  `builder.Navigation(stats => stats.Mrr).UsePropertyAccessMode(PropertyAccessMode.Field)` placed after
  `HasMany`, which is the EF Core API that does not depend on the navigation already having been
  discovered.

### 3.3 Tests that could not fail (STATUS 3568–3569, 3610–3611, 3629–3630, 3666–3667, 3144–3145, 3587–3588, 3909–3911)

- **The `AnalyticsRead` boundary tests.** STATUS counts three; the code has two assertions of the
  boundary (`SupportAgentCannotReadTheSeries`, `SupportAgentIsRefusedTheDashboardButKeepsTheAuditFeed`),
  both 403-only. The "three grants" are the three non-SuperAdmin roles that hold `analytics.read`:
  PlatformAdmin, BillingAdmin and ReadOnly. Each of the two tests gains a positive control for each of
  those three roles in the same test — 200 from the same URL — so deleting any one of the three grants
  turns a test red. `SuperAdmin` is not a control: it grants everything and proves nothing about the row.
- **The null-features fake.** `GymFakes.GetAsync` returns `features: null` when `includeFeatures` is
  true and no `FeatureQueries` fake was wired — a shape production never produces. The fake now throws
  `InvalidOperationException("Test requested features without wiring FeatureQueries")` in that case,
  and the six gym write-route unit tests (`GymTestContext` consumers that call `ReadBackAsync`) wire the
  feature fake and grant or withhold `features.read` so `GymWrites.ReadBackAsync`'s gate is exercised
  both ways in at least one test per route.
- **JPY.** `AZeroDecimalCurrencyRoundsToWholeUnits` uses 12,000 yearly, evenly divisible by 12. It
  becomes 12,005 → expected 1,000 (banker's or away-from-zero both give 1,000; the test also asserts the
  remainder is not carried — a second case, 12,007 → 1,001, pins the rounding direction the domain
  already implements).
- **`DeleteGymCommandHandler`.** A unit test in `GymOS.Platform.Application.UnitTests` asserts the
  handler calls `IFeatureExceptionMaintainer.WithdrawRedundantAsync` for the deleted gym exactly once.

### 3.4 Concurrency (STATUS 3630–3631, 3667–3668, 3892–3896, 3964–3982)

- **`AssignSubscriptionCommandHandler`.** The handler has no transaction today, so the `UPDLOCK` a
  `FindByIdForUpdateAsync` takes would be released at the statement's end. H1 gives the handler one on
  the `SetGymFeaturesCommandHandler` / `SubscriptionRenewalService` precedent (`await using` the
  `IUnitOfWorkTransaction`, commit after the final `SaveChangesAsync`), and the plan read at
  `SubscriptionCommands.cs:127` becomes `FindByIdForUpdateAsync` inside it, so a concurrent plan feature
  edit cannot change what "redundant" means between the read and the withdraw. The early-return
  failures (gym not found, plan not found, inactive plan) happen before or inside the transaction and
  simply dispose it without committing — nothing has been written. **Verified by unit test**, not by a
  racing integration test: `FakePlanRepository` records which reads were locked, and
  `SubscriptionAssignmentTests` asserts one transaction opened and the plan read taken `ForUpdate`. A
  race that passes whenever two requests happen to serialise proves nothing (STATUS 3897–3899 records
  exactly that shape at 1-in-8 detection), and the interleaving hook that would make it deterministic is
  out of scope here.
- **`DiscardTrackedChanges`.** `BannedSymbols.txt` gains
  `M:GymOS.BuildingBlocks.Application.Abstractions.IUnitOfWork.DiscardTrackedChanges;Clears the whole scope's change tracker — only the SetGymFeatures retry may call it, see GymFeatureCommands`.
  The one call site carries `#pragma warning disable RS0030` / restore around the call with the reason
  on the same lines, matching how the repo already suppresses banned symbols at a justified site (the
  M7 `[SuppressMessage]` convention applies to analyzer rules; RS0030 is suppressed inline because the
  attribute form does not scope to a statement). `IUnitOfWork`'s XML doc names the ban.

### 3.5 API-reference launcher (STATUS 3676–3681)

- **Seam.** `AddGymOSApiReferenceBrowser` takes an optional `Func<string, string?> readEnvironmentVariable`
  defaulting to `Environment.GetEnvironmentVariable`. `ApiReferenceBrowserRegistrationTests` gains two
  cases: in-container → not registered; not in container → registered.
- **`Process.Start` null.** `ApiReferenceBrowserLauncher` logs `LogOpened` only when the returned
  `Process` is non-null; a null result logs a new `LogHandedToExistingHandler` line at Information
  (EventId 6103): on Windows, `UseShellExecute` returns null whenever the URL was handed to a browser
  that was already running, which is the common case, so this is not a warning — it is the honest
  version of what "opened" claimed.
- **Doc.** The method's XML comment says: three guards decide the *default*; `OpenApi:LaunchBrowser`
  set explicitly overrides all three, and that is intended — it is the switch a developer flips inside a
  container on purpose.

### 3.6 Small items (STATUS 3887–3891, 3905–3907, 3920–3922, 4009–4014)

- **Withdrawn count.** The four synchronous callers of `WithdrawRedundantAsync` (gym delete, assign,
  change plan, renewal) log the returned count at Information when it is greater than zero, with gym id
  and cause, via one shared `LoggerMessage` in `FeatureExceptionLogging`.
- **`StoredException`.** The `[SuppressMessage("Design", "CA1711")]` on `GymFeatureExceptions.cs:19`
  goes; `.editorconfig`'s `dotnet_code_quality.CA1711.allowed_suffixes` gains `Exception` beside
  `Permission`, with the comment extended to name `StoredException` as a stored record, not a throwable.
- **`ActivityEntry.EntityType`.** `DashboardQueries.GetRecentActivityAsync` projects `log.EntityType`
  as-is (nullable) so it agrees with `AuditLogQueries`; the DTO property becomes `string?`; the OpenAPI
  snapshot updates for that one nullable.
- **Dev overlay ports.** Every `ports:` entry in `docker-compose.override.yml` binds to loopback:
  `'127.0.0.1:${API_PORT:-5080}:8080'` and likewise SQL Server, Mailpit, Redis, Seq, MinIO. `DOCKER.md`
  notes it and the one-line override for a developer who needs LAN access.

### 3.7 Docs only

- **STATUS.md.** The two churn paragraphs (3932–3951) and the `canceled_at_utc` clause (3610) are
  rewritten as resolved at `7672bb6` with the date. The "POST /users/{id}/reset-password not
  implemented" row (4036–4039) is marked resolved with a pointer to `UserEndpoints.cs`. The
  `/dashboard/health` finding (3563–3565, 3603–3605) is closed as **kept by decision**: the gate exists
  so an unauthenticated caller cannot probe the database's state; the cost is a 500 instead of
  "Unhealthy" when the database is down, and `/health/ready` is the anonymous signal for that case.
- **`docs/BusinessRules/Analytics.md` §11.** The two limitation entries about `CurrentPeriodEndUtc`
  become a record of the fix: `ExpiredAtUtc` stamped by the renewal sweep, `expired_gyms` keyed on it,
  both churn columns on the same clock since `7672bb6`, with the residual note that a subscription
  expiring while the sweep is disabled is counted on the day the sweep catches up, not the day the period
  ended — which is now the stated definition, not a defect.
- **Collation.** `docs/Infrastructure/DOCKER.md`'s collation paragraph (around line 80, which already
  names "the one behavioural" consequence) gains the fifth difference: `idempotency_key` compares case-insensitively under
  `Latin1_General_100_CI_AS`, so two keys differing only in case are one key on SQL Server and were two
  on PostgreSQL.
- **`IdempotencyRecordConfiguration`** XML doc: remove the `COALESCE` sentinel and the deleted
  `AddIdempotencyRecords` migration reference; point at `20260823193131_RuntimeDatabaseProgrammability.cs:113`
  and say SQL Server's unique index treats NULLs as equal, which is why no sentinel is needed.
- **`DOCKER.md` §1 diagram** (line 30): `mailpit` moves out of the "profiles (off by default)" box into the
  always-on services, matching the compose file since ADR-53.
- **`IDashboardHealthCheck`** comment: "the one place" → name the two places (`Program.cs` wiring and
  the health-check registration) or drop the uniqueness claim.

## 4. Out of scope, stated

Grants spec steps 3–5; `FeatureExceptionMaintainer`'s unlocked read (call-site audit first);
value-vs-intent; `GET /features` subqueries; the plan-edit race test's interleaving hook; the UTC-midnight
race in `ADeletedGymLeavesThePopulationButStillCountsAsChurn` (needs an `IClock` seam in
`AuthenticationHost`, its own small piece of work); the three Minor M4 items (`PlanQueries.GetAsync`
subqueries, `FeatureRepository.ListAllAsync` tracking, `LogSynced` orphan count); dependency majors;
every owner item.

## 5. Verification plan

**Unit (Infrastructure.UnitTests; `InternalsVisibleTo` exists).** `PermissionEvaluator` with a fake
snapshot: null tenant → platform set; tenant A → union; suspended user → empty regardless of tenant.
`CallerSnapshot` with a fake `IIdentityQueries`: `GetIdentityAsync` then `GetAsync` for the same user →
one identity query; two `GetAsync` for the same (user, tenant) → one permissions query; a different
tenant → a second permissions query. `SubscriptionAssignmentTests`: one transaction, plan read locked. `MonthlyRecurringRevenue` JPY 12,005 and 12,007. Summary handler
`mrr` order. `DeleteGymCommandHandler` calls the maintainer once. Six gym write-route tests exercise
`features.read` both ways. `ApiReferenceBrowserRegistrationTests` in/out of container.

**Integration (real SQL Server, the leased-database pattern).** Grants tests 1–5 (§2.4), 1, 2 and 5
red-first. `SuperAdminSeedingTests`: two concurrent seeders, one user. Three `AnalyticsRead` tests with PlatformAdmin positive controls. `DashboardApiTests`
summary unchanged in values after the query rewrite (existing tests). `MaintenanceModeTests` still pass
with the middleware order changed (SuperAdmin passes on a gym route too — one added case).
`ApiEndpointTests` secured count stays 65. `OpenApiContractTests` snapshot updated once for
`EntityType` nullability.

**Whole suite.** One `dotnet test GymOS.Backend.slnx` at the final commit, `dotnet format --verify-no-changes`,
CI green, `Deploy to UAT` green (no migration in H1).
