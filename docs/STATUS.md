# GymOS — Implementation Status & Continuation Notes

**Last updated:** 2026-08-10 · M3.3 complete, all 7 tasks (branch `m3.3-invoices`, not merged)
**Purpose:** cross-device continuation state. Read this + `GymOS-Specification/README.md`
before resuming work on any machine.

---

## Where we are

| Milestone | State |
|---|---|
| Architecture (ADRs 1–49, frozen) + amendments (ADR-50 .NET 10, ADR-51 superseded, ADR-52 repo map) | ✅ in GymOS-Specification |
| **M0 — engineering foundation** (skeleton, architecture/package law, kernel + CQRS dispatcher, PostgreSQL/EF/outbox, Docker, logging/correlation/OTel, Testcontainers, CI) | ✅ complete |
| **M1.1 — identity domain** (User/Role/Permission/RefreshToken aggregates, platform schema migration, declare-and-validate seeder) | ✅ complete — 69 tests green |
| **M1.2 — auth services & endpoints** (Argon2id, JWT + rotating refresh tokens, `/api/v1/auth`, SuperAdmin seed) | ✅ complete — 159 tests green, verified live |
| **M1.3 — permission pipeline, users/roles endpoints, audit interceptor, OpenAPI diff gate** | ✅ complete + release-reviewed — **256 tests green**, verified live |
| **Pre-M2 hardening** (write convention, optimistic concurrency, business-rule framework, API standards, playbook) | ✅ complete — **337 tests green**, verified live |
| **M2 — gym management** (gyms CRUD, lifecycle, branding, gym detail) | ✅ complete — 541 tests green at the time, verified live · ⬅ **awaiting review** |
| **`Money` (AD-42)** — currency-checked arithmetic, explicit rounding, largest-remainder `Allocate`, EF mapping, rule R4 | ✅ complete — **645 tests green**, storage contract checked against PostgreSQL · ⬅ **awaiting review** |
| **Members domain** (business specification, no code) | ✅ **FROZEN** — `docs/Domain/MEMBERS-DOMAIN-DESIGN.md`. 11 product decisions approved 2026-08-05; implementation may begin. 4 legal-review items deferred (mechanism to build, values from counsel). |
| **M3.1 — subscription plan catalogue** (plans, limits, trial, prices; `Money`'s first consumer) | ✅ complete — **734 tests green**, verified live · ⬅ **awaiting review** |
| **M3.2 — subscription assignment** (gym_subscriptions, lifecycle, cancellation, payment state) | ✅ complete — **853 tests green**, verified live · ⬅ **awaiting review** |
| **AD-39 idempotency framework** (claim table, pipeline behavior, decision table, host plumbing) | ✅ complete — awaiting review |
| **M3.3 — invoices** (Invoice aggregate, per-year numbering, issuing on assignment/conversion/upgrade, net-sign proration, mark-paid as the first production `IIdempotentCommand`, void, §3.4 endpoints) | ✅ **merged to `main`** (`e39eb73`) — reviewed at high effort (10 findings, all fixed), **1049 tests green**, verified live incl. idempotent replay |
| **M3.4 — job runner + renewal/overdue jobs** (JobHost + advisory locks, RetentionJob, renewal sweep, overdue sweep with auto-PastDue) | ✅ complete on branch `m3.4-jobs` — design approved 2026-08-10, all tests green, verified live · ⬅ **awaiting review, not merged** |
| **F1 — Super Admin console** (GymOS-Platform repo, `v0.1.0`) | ✅ complete — frontend of M0–M2; 22 unit + 20 E2E green |
| **M0.X — production Docker & Compose** (multi-stage image, dev/prod overlays, hardening, `docs/Infrastructure/DOCKER.md`, CI docker job) | ✅ complete — clean clone → `docker compose up -d` → healthy + working login; production overlay smoke-tested · ⬅ **awaiting review** |
| M4+ — per approved roadmap (feature flags, dashboard, monitoring, settings) | later |

## M1.2 as built

All five agreed items landed. No schema change was needed — M1.1's `users` and
`refresh_tokens` tables already carried every column, so **there is no new migration**.

1. **Argon2id hasher** — `Argon2PasswordHasher` (Platform.Infrastructure), cost from
   `Security:PasswordHashing`. Hashes are stored in **PHC string format**
   (`$argon2id$v=19$m=…,t=…,p=…$salt$hash`) and verification reads its parameters *out of
   the stored hash*, so raising cost affects new passwords only and never locks anyone out.
2. **JWT access tokens** (`JwtTokenService`, 15 min; claims `sub`, `email`,
   `security_stamp`, `perm_hash`) + **rotating refresh tokens** (`RefreshTokenGenerator`,
   32 random bytes base64url, SHA-256 at rest, httpOnly/Secure/SameSite=Strict cookie
   scoped to `/api/v1/auth`). Replaying a rotated token revokes its **descendants only** —
   other devices hold their own chains and stay signed in.
3. **`/api/v1/auth`** via `Asp.Versioning.Http`: `login`, `refresh`, `logout`, `me`,
   `change-password`. RFC 7807 everywhere, with the stable machine code in a `code`
   extension member. Errors are deliberately coarse (`auth.invalid_credentials` covers
   unknown email, wrong password, and un-activated invitation) so login is not an
   account-existence oracle; the sign-in path hashes even for unknown emails to keep
   response time flat.
4. **Initial SuperAdmin seed** — `SuperAdminSeeder`, config-only, **creates but never
   resets**, refuses a password below the policy, warns and does nothing when unset.
5. **Integration tests** — 32 over real HTTP + real PostgreSQL: full
   login → refresh → reuse-detection → logout, `/me`, change-password, forged token,
   seeder behavior, rate limiting, OpenAPI presence.

### Decisions taken during M1.2 (worth knowing before M1.3)

- **Rate limiting is two mechanisms, not one.** `Security:RateLimiting` caps sign-in
  attempts **per client IP** (ASP.NET fixed window, 429 + `Retry-After`); the domain
  lockout caps them **per account**. A single composite IP+email key would need the
  request body read inside the partition selector, before model binding — buffering every
  sign-in to duplicate protection the lockout already gives. Documented in
  `RateLimitingExtensions`.
- **`IUnitOfWork` + repositories, not `IAppDbContext`.** AD-3's `IAppDbContext` cannot be
  expressed here: `ApplicationLayers_DoNotDependOnAnyInfrastructure` bans
  `Microsoft.EntityFrameworkCore` in Application, so a `DbSet<T>`-typed interface is
  impossible. Write access goes through `IUserRepository`/`IRefreshTokenRepository`, reads
  through `IIdentityQueries` projections, and one `IUnitOfWork.SaveChangesAsync` per use
  case keeps state + outbox + audit in one transaction.
- **OpenAPI paths are substituted.** `AddGymOSOpenApi` rewrites
  `/api/v{version}/…` → `/api/v1/…` and drops the redundant `version` parameter, so
  generated clients and the future diff gate see real paths. **The M1.3 snapshot gate
  should snapshot the transformed document.**
- **Change-password ends every session, the caller's included** — stamp rotation kills
  access tokens, and the handler revokes all refresh tokens so a thief cannot refresh
  back in. Clients must re-authenticate.
- **Deferred deliberately:** rehash-on-login when cost parameters change (needs a domain
  method that upgrades the hash *without* rotating the security stamp, or it would sign
  everyone out); an absolute cap on sliding refresh lifetime (needs a chain-root column);
  `security_stamp` *enforcement* — the claim is issued, but rejecting a stale stamp
  belongs to the M1.3 permission pipeline. Refresh already re-reads account state, so a
  suspension still takes effect within one access-token lifetime.
- **`global.json` now pins SDK `10.0.201`** (was `10.0.301`, which no SDK on this machine
  satisfied — `rollForward: latestFeature` only moves *up*). Machines with 10.0.3xx still
  use it; this is a floor, not a ceiling.

## M1.3 as built

Adds two migrations: `AddAuditLog` (creates `platform.audit_logs`) and `PartitionAuditLogs`
(converts it to monthly range partitions).

1. **Permission pipeline, both layers (AD-5).** Endpoint policies are produced on demand by
   `PermissionPolicyProvider`, so `RequireAuthorization("users.manage")` needs no
   registration list; `AuthorizationBehavior` re-checks the `[RequiresPermission]` on the
   request type. Both read the same `IPermissionEvaluator`, so they cannot disagree.
   Permissions come from the database, not from token claims — a revoked role takes effect on
   the caller's *next request* instead of at token expiry.
2. **Security-stamp enforcement** (closes the M1.2 deferral). `OnTokenValidated` compares the
   token's `security_stamp` against the stored one and refuses a signed, unexpired token
   whose account has changed. Suspension now ends sessions *immediately*, not within one
   access-token lifetime. `ICallerSnapshot` memoizes the read per request, so the validator,
   the endpoint policy, and the pipeline share one query.
3. **`/api/v1/users`** — list (paged/filtered/sorted), detail, invite, update, suspend,
   reactivate, set-roles. **`/api/v1/roles`** — read-only, because seeded role grants are
   reconciled to the code on every startup and an edit endpoint would be overwritten on the
   next deployment. **`/api/v1/audit-logs`** — read-only, permanently.
4. **Audit interceptor (AD-7).** Entity mutations captured in the same transaction as the
   change; sign-in outcomes recorded explicitly via `IAuditTrail`. Auditing is **opt-in per
   entity** (`PlatformAuditedEntities`) and **redaction is opt-out** — any property whose name
   matches `password|secret|token|hash|stamp|signingkey|apikey|credential|privatekey` is
   masked unless explicitly exempted, so a sensitive field added years from now is masked
   without anyone remembering this file exists.
5. **OpenAPI snapshot gate (R13).** `contracts/openapi-v1.json` is committed and diffed by
   `OpenApiContractTests`. Regenerate deliberately:
   `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests`, then read
   `git diff contracts/openapi-v1.json`. It fails on *any* change by design — the review is
   the gate, not a tool guessing which changes are breaking.
6. **Outbox `user_id` is now stamped** from `ICurrentUser` (closes the M1.2 open item).
7. **Audit partitioning** (added by the release review) — monthly range partitions on
   `created_at_utc`, a maintenance function creating months ahead of need, and a DEFAULT
   partition so an audit write can never fail. See `docs/RELEASE-NOTES.md`.
8. **Account lifecycle** (added by the release review) — `UserStatusTransitions` is now the
   single transition table; `User` routes every status change through it. `Locked` and
   `Deleted` are deliberately *not* states, with the reasoning recorded on that type.
9. **Forwarded headers** (added by the release review) — opt-in, topology-declared, off by
   default. Required in any proxied deployment or the rate limiter degrades to one bucket for
   all callers and audit rows all record the proxy's address.

### Decisions and traps found during M1.3

- **A Guid PK that the code assigns must be `ValueGeneratedNever()`.** EF's default for a Guid
  key is `ValueGeneratedOnAdd`, and for an entity discovered through a *navigation* rather
  than an explicit `Add`, EF reads "key already has a value" as "row already exists" and
  tracks it **Modified** instead of **Added**. The UPDATE matches nothing and surfaces as
  `DbUpdateConcurrencyException` — an error that points at optimistic concurrency, which is
  not the problem. Fixed for all entities at once by
  `GymOSModelConventions.ApplyClientAssignedKeys()`, called at the end of `OnModelCreating`.
  **Any new DbContext must call it.**
- **Enum `.ToString()` and record constructors do not belong in an EF projection.**
  `Scope.ToString()` has no SQL translation, and a correlated collection projected straight
  into a positional record constructor does not translate either. Both fail at *runtime*.
  Project into an anonymous type and shape the DTO in memory (see `RoleQueries`).
- **`PermissionDeniedException` is thrown, not returned.** Business failures are `Result`
  values, but reaching a handler without its permission means the *route* is misconfigured —
  a defect. The host maps it to 403 so the caller still sees the right answer, while the
  behavior logs it as an error naming the request.
- **Cross-aggregate invariants live in the Application layer.** "The last active SuperAdmin
  cannot be demoted or suspended" is a constraint over the *set* of users; enforcing it inside
  `User` would mean handing an aggregate a repository. `SuperAdminGuard` centralizes it so
  both callers apply the identical check.
- **Test isolation is not automatic.** xUnit builds a fresh test-class instance per method, so
  a fixed Testcontainers database name silently shares state across a class — and a test that
  counts rows passes or fails on execution order. Suites whose assertions depend on the rows
  present use `PostgresFixture.CreateIsolatedDatabaseAsync`.
- **`WebApplicationFactory` loads the host project's user secrets.** A developer's local
  `Seed:SuperAdmin` therefore leaks into tests; blank the keys explicitly rather than removing
  them from the settings dictionary (`UseSetting` can override, not unset).
- **Known nuance:** `auth.signed_in` audit rows have `user_id` null — at sign-in the caller is
  not yet authenticated, so `ICurrentUser` is empty. The user is identified by `entity_id`
  and `user_email`. Filtering the trail by `userId` will not return sign-ins.

## Pre-M2 hardening as built

Read `docs/M2_READINESS.md` for the full assessment; the short version:

1. **Write responses** return the resource (`201`/`200`), not `204`. One helper, `WriteResults`,
   owns the convention. Three documented exceptions.
2. **Optimistic concurrency** everywhere: `rowVersion` + strong `ETag`, `If-Match` required on PUT
   (`428` without), `409` with `retryable: false` on a stale version, `400` on a malformed one.
   **A write that changes only child rows must call `MarkAggregateChanged`** or the aggregate's
   version never moves — that gap was real and is now covered by a test.
3. **Business rule framework** in SharedKernel (`IBusinessRule`, `BusinessRules`,
   `BusinessRuleViolationException`) plus `BusinessRuleAssert`. `DomainException` is unsealed so a
   broken rule is one exception strategy, not two. Adopted for a real rule, not left speculative.
4. **`docs/BusinessRules/`** — 11 module documents, drafts with assumptions marked `(?)`.
5. **`docs/Engineering/API_STANDARDS.md`** and **`docs/ENGINEERING_PLAYBOOK.md`** are the standing
   references for anything the tests cannot check.
6. **R9 enforced**: `DomainEventConventionTests` checks event naming, versioning, uniqueness, and
   immutability across every production assembly.

## M2 as built

One migration: `AddGyms` (`platform.gyms` + `platform.gym_branding`). Business rules and the deferral
list live in **`docs/BusinessRules/Gyms.md`** — a specification, not a draft.

1. **`Gym` aggregate** (platform schema, **no `tenant_id`** — a gym *is* the tenant, so the row is the
   tenant's definition rather than a row belonging to one). `GymStatusTransitions` is the single
   transition table, tested as an exhaustive matrix. `Deleted` is terminal, per §2.2.
2. **`GymBranding` as its own aggregate**, 1:1 by `GymId`. Branding is edited by different people at
   different times than the gym's commercial details, every field is optional while none of the gym's
   are, and a logo change should not appear in the trail as a change to ownership. It is created empty
   alongside the gym, so no caller needs a create-or-update path.
3. **`/api/v1/gyms`** — list (paged, filtered by status/country/search, deleted excluded by default),
   detail, provision, update, `activate`, `suspend`, `reactivate`, `DELETE`, `PUT /branding`. All seven
   value rules are rule objects with stable codes (`gyms.*`), so a form can highlight the field that
   failed.
4. **`X-Confirm-Action` is now plumbed** (AD-6, its first use): `ConfirmationHeaders.ConfirmAction`
   reads it, and the handler compares it to the gym's slug — a header, not a body field, so it survives
   `DELETE` and so a replayed script that never saw the resource cannot satisfy it.
5. **`Gym` and `GymBranding` are audited** (`PlatformAuditedEntities`). Verified live: the four
   `gym.updated` entries are activate/suspend/reactivate/delete, and the branding-only write correctly
   produced *no* bare-timestamp entry on the gym while recording `gym_branding.updated`.
6. **Permissions**: `gyms.read|create|update|suspend|delete`. `PlatformAdmin` deliberately holds
   suspend but **not** delete — suspension is reversible and deletion is not.

### Decisions and traps found during M2

- **`POST /gyms/{id}/activate` is not in the architecture's endpoint table**, which lists only
  suspend/reactivate/delete. It exists because `Provisioning=1` is a real state in the frozen schema and
  something must move a gym out of it; without it, `Provisioning` would be a dead value no gym could
  leave. When subscription assignment lands in M3 it becomes the natural completion of provisioning.
  This is an addition to a summary table, not a change to a decision — no ADR needed.
- **`DELETE` returns 204, not the soft-deleted resource.** The convention (API_STANDARDS.md §2) says
  `DELETE` → 204, and adding an exception needs a better reason than "the row still exists". `GET` on a
  deleted gym still resolves, which is where an operator sees the recorded deletion.
- **Rules must run on the normalized value, not the raw one.** `Gym.Provision` originally validated
  `owner.Email` before `Normalized()`, so an address pasted with a trailing space was rejected as
  malformed. Caught by a test; the fix normalizes first in both `Provision` and `UpdateDetails`. Worth
  remembering for every future aggregate that trims input.
- **`varchar`, not `char(n)`, for ISO codes.** `bpchar` compares ignoring trailing spaces, so a padded
  value would equal a clean one in an index. Length is a domain rule.
- **`citext` was not adopted for the slug.** Lowercase-by-construction + a unique index + a
  `slug = lower(slug)` CHECK satisfies "unique, URL-safe, immutable" without adding an extension to
  every environment. §2.2 says `citext`; this is an implementation detail meeting the stated property.
- **`dotnet ef migrations remove` needs a reachable database** — it connects to check whether the
  migration was applied. With no database up, restore `AppDbContextModelSnapshot.cs` from git and delete
  the two migration files by hand, or the next `add` produces an empty migration.
- **A hard-coded count in a test is a liability.** `IdentitySeedingTests` asserted SupportAgent had
  exactly 2 permissions and broke when it gained `gyms.read` — a correct change. It now compares against
  `SystemRoles.Definitions`, so it verifies the seeder reproduced the declaration instead of failing
  because a number moved.
- **`Money` (AD-42) was not needed and is still owed before M3.** A gym's currency is an ISO *code*,
  not an amount; nothing in M2 holds a monetary value. Subscriptions do.
- **RLS (R14) was not needed and is still owed with the first tenant-scoped entity.** Gyms are
  platform-scope and carry no `tenant_id`, so there is no policy to write here. Members is where R14
  first has to run.

## `Money` (AD-42) as built

No migration and no consumer yet — deliberately. Built while no table holds a monetary column, because
that is the only time the shape is free to change.

1. **`Currency`** — ISO 4217 code plus **minor units**, which is the fact the code alone does not carry.
   The exception table lists every currency that is *not* hundredths (JPY 0, KWD 3, CLF 4); everything
   else defaults to 2. A test restates that list independently, so two lists must agree.
2. **`Money`** — `readonly record struct`, rounded to minor units on construction, currency-checked
   arithmetic (`CurrencyMismatchException` on EUR + USD), `Multiply` with a **required**
   `RoundingPolicy`, and `Allocate(params int[])` by largest remainder.
3. **`Allocate` is the reason this type exists.** €100 three ways is 33.34/33.33/33.33; the property
   `sum(parts) == whole` is asserted over amounts smaller than the part count, negatives, zero, lopsided
   ratios, and currencies with 0 and 3 decimals. A credit splits as the exact negation of the charge it
   reverses, so the spare cent lands on the same party both times.
4. **Persistence** — `MoneyConventions.HasMoney(…, "total")` maps an EF **complex type** to
   `total_amount numeric(14,4)` + `total_currency varchar(3)`. Currency stores only the code; minor units
   are derived, because a stored copy could go stale and claim EUR has three decimals.
5. **R4 is now enforced** (`MoneyTypeTests`) — its first subject. A `decimal` member named like money
   outside `Money` fails the build. **Verified by introducing a violation and watching it fail**, then
   removing it; an architecture rule nobody has seen fail is a rule nobody knows works.

### Decisions taken while building `Money`

- **`Currency.Of` validates shape, not membership.** Three ASCII letters, no list of currencies that
  exist — a membership check would refuse a real currency the day ISO adds one, and an invented code
  fails everywhere else anyway. The risk worth guarding is a *real* currency with the wrong number of
  decimals, which is what the exception table is for.
- **Arithmetic is methods, comparison is operators.** `a + b` reads as something that cannot fail;
  `a.Add(b)` reads as something with a rule behind it. Comparison has no policy to choose, so `<` stays.
- **No currency-less zero.** `Money.Zero(currency)` requires one, because a currency-less zero would be
  the single value addable to anything — exactly the silent coercion AD-42 exists to stop.
- **A `default(Money)` throws rather than behaving like €0.** A struct field initialises to it with no
  constructor running, and "no currency" must not round or compare.
- **Deferred with reasons:** `ApplyTax` / `TaxRate` / `TaxRoundingPolicy` (AD-42 names them) — the
  per-line-versus-per-total distinction cannot be honoured by a method on a *single* amount, so it
  belongs where invoice lines exist, and the jurisdiction rules need accounting counsel first.
  Intermediate 4-decimal precision likewise waits for its first producer (currency conversion, AD-43);
  the column already has the room, so adding it is additive. `ICurrencyService` and
  `IExchangeRateProvider` (AD-43) are untouched — the first conversion is platform MRR normalization
  in M5.
### ⚠️ The test host on this machine aborts intermittently — read before trusting a test run

`dotnet test` dies with `Internal CLR error (0x80131506)` partway through a run. **Confirmed not ours:**
`git stash -u` back to the last commit reproduces it identically, and it survives
`DOTNET_TieredCompilation=0`. SDK 10.0.201, runtimes 10.0.3 and 10.0.5 both installed.

Two traps it sets:

1. **The crash stack names whichever test was mid-flight**, which looks exactly like a bug in that code.
   It pointed at `UpdateGymBrandingCommandHandler` on one run and at an untouched M1 test on the next.
   Do not debug the named method before reproducing it in a *completed* run.
2. **An aborted run still prints `Passed!` with a truncated count.** `Passed! … Total: 18` on a project
   with 103 tests is a crash, not a pass. **A run is only trustworthy if its output contains no
   `Aborted`** and the total matches the project's known count.

Longer runs fail more often, so the two largest projects (`Platform.Application.UnitTests` 103,
`IntegrationTests` 102) rarely finish in one go. Verify them class by class:

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~GymApiTests"
```

Full counts per project, for checking a run was complete: SharedKernel 126 · BuildingBlocks 36 ·
Architecture 120 · Domain 242 · Application 147 · Infrastructure 67 · Integration 151 = **889**.

Capping the test connection pools (above) may also have helped here — the last several full runs completed
first time, where they previously needed retries.

## M3.1 as built

One migration: `AddSubscriptionPlans` (`platform.subscription_plans` + `platform.plan_prices`). Rules and
the full deferral list: **`docs/BusinessRules/Subscriptions.md`**.

**M3 is four increments, and that scoping is the main decision here.** Three of its four parts have
prerequisites that do not exist:

| Increment | State | Blocked on |
|---|---|---|
| M3.1 plans + prices | ✅ built | — |
| M3.2 subscription assignment | next | M3.1 only |
| M3.3 invoices | blocked | **AD-39 idempotency framework.** A retried "mark invoice paid" must not pay twice; this is the first genuinely qualifying command the platform has had. |
| M3.4 renewal / overdue jobs | blocked | **A job runner.** Nothing yet runs work on a schedule. |

`plan_features` (§2.3) moves to **M4**: it points at the `features` catalogue, which does not exist.

1. **`SubscriptionPlan`** — code (immutable, upper-case identifier), name, description, trial days,
   nullable capacity limits, sort order, and `IsActive`/`IsPublic` as separate questions: inactive means
   "cannot be newly assigned", unlisted means "assignable but not advertised" (a negotiated plan).
2. **`PlanPrice` holds a `Money`** — one row per (cycle, currency). `PUT /plans/{id}/prices` upserts,
   because "the annual euro price is €480" is one intent and making the caller check first invites the
   race where two operators both add a row.
3. **`BillingCycle` values are month counts** (1/3/12), so a period's length is the enum value itself.
4. **Plans are never deleted** — §3.4's "delete = deactivate only". `DELETE /plans/{id}` is 405, asserted.
5. **`billing.read` / `billing.manage`** — `PlatformAdmin` gets read but not manage; `BillingAdmin` gets
   both. What the platform charges is a commercial decision, not day-to-day administration.

### Decisions and traps found during M3.1

- **A `Money` change was silently unaudited — found in the live run, now fixed.**
  `EntityEntry.Properties` **omits complex-type members**; they live under `ComplexProperties`. So a write
  that only changed a price left the interceptor with an empty change set, which its
  "nothing but a timestamp moved" suppression then dropped. A plan repriced €50.00 → €59.99 had **no record
  of who did it** — the exact gap the audit trail exists to prevent, in the exact place I had claimed it
  mattered. The interceptor now walks complex properties recursively and names fields by path
  (`Price.Amount`), which also keeps a future invoice's `Net.Amount` and `Tax.Amount` apart. Pinned by an
  integration test that fails without the fix.
- **EF cannot index inside a complex property.** `HasIndex(..., "Price_Currency")` fails with "no
  corresponding CLR property or field", so the (plan, cycle, currency) unique index is **raw SQL in the
  migration**, documented there and asserted by a test that reads `pg_indexes`. Any future change to the
  price key must be made the same way.
- **`Money` has no wire form, and that is why R4 has three exemptions.** `Currency` keeps its code in a
  private field behind a property that throws when unset, so System.Text.Json would quietly produce a
  default rather than fail. At serialisation boundaries — event payloads, API DTOs — money travels as an
  amount beside an explicit currency code. A `JsonConverter` belongs with the first consumer that needs one.
- **The integration suite outgrew PostgreSQL's connection limit.** Each test host opened a default
  100-connection pool against a container allowing 100 clients; the symptom was
  `53300: sorry, too many clients already` in whichever unrelated test ran when the ceiling was reached.
  Pools are capped at 5 per host and the container now allows 300. Worth remembering as the suite grows.
- **`dotnet run` ignores `ASPNETCORE_URLS`** when `launchSettings.json` is present — it listened on 5080
  while I polled 5081. Pass `--no-launch-profile`, or just use 5080.
- **`launchSettings` aside: a leftover `GymOS.WebApi` process locks the build output.** Two builds failed
  with MSB3027 until it was killed. If a build suddenly cannot copy DLLs, look for a running host first.
- **The verify harness (`verify.sh`) runs `--no-build`** — build first, or it tests stale binaries and
  reports failures that no longer exist. That cost two rounds of confusion.

## M3.2 as built

One migration: `AddGymSubscriptions`. Rules and the deferral list: **`docs/BusinessRules/Subscriptions.md`**.

1. **`GymSubscription`** — one **live** row per gym enforced by a partial unique index
   (`gym_id WHERE status IN (1,2,3)`); ended rows are kept, because they are the record a billing dispute
   is settled from.
2. **Lifecycle** `Trialing → Active ⇄ PastDue`, with `Canceled` and `Expired` terminal and distinct:
   cancellation is a decision (what churn counts), expiry is a failure to collect. Collapsing them would
   make M5's churn figure wrong in a way nobody could untangle.
3. **Seven endpoints** under `/api/v1/gyms/{gymId}/subscription`, plus `subscriptions.manage` — separate
   from `billing.manage`, because deciding what to sell and deciding what one customer pays are different
   authorities.
4. **Two earlier deferrals closed**: gym detail now carries `subscription`, and plan responses carry
   `subscribedGymCount`. Both were omitted while they could only have read null or zero.

### Decisions and traps found during M3.2

- **The spec's "price snapshot FK" cannot be an FK.** §2.3 says `plan_price_id` is the snapshot, but the
  same section's `(plan_id, billing_cycle, currency)` uniqueness forces prices to be *mutated in place* —
  so a pointer would silently reprice every existing subscription, the opposite of "renewals honor the
  price at assignment". The amount, currency, and cycle are **copied**; the FK survives as nullable
  provenance with `ON DELETE SET NULL`. Verified live. **Honours the intent; the literal mechanism is
  unworkable against its own constraint.**
- **Scheduled cancellation must not look like cancellation.** `cancelAtPeriodEnd` leaves the subscription
  `Active` and live — the gym paid to the end of the period. A consumer that revoked access on the
  *scheduling* event would cut off a paying customer, which is why the two are separate events.
- **`PastDue` does not withhold service.** That is a gym suspension. Conflating them would mean every
  failed card ended a business's day.
- **Changing plan updates the one row.** The status enum has no "replaced" state, so writing a `Canceled`
  row for an upgrade would inflate churn. History lives in the audit trail and the event carries both
  plan ids.
- **R4 now encodes its principle rather than a list.** A money-shaped `decimal` is allowed when its type
  is a record *and* declares a currency member. This replaced six by-name exemptions and pre-empted four
  more. **Verified with two decoys** — an aggregate with a `decimal MonthlyFee` is still caught because
  it is a class; a record with an amount and no currency is still caught. Residual risk documented at the
  rule: a domain value object written as a record could pair a decimal with a currency string.
- **Proration moved M3.2 → M3.3** (correcting my own earlier note). `Money.Allocate` is ready, but a
  prorated charge needs an invoice to land on.
- **A leftover `dotnet run` host locks the build output.** Two builds failed with MSB3027 again. If a
  build cannot copy DLLs, look for a running `GymOS.WebApi` first — this has now cost time twice.

## M0.X — production Docker infrastructure as built

Full reference: **`docs/Infrastructure/DOCKER.md`**. Shape: `docker-compose.yml` (environment-neutral
topology, no credentials, no published ports) + `docker-compose.override.yml` (development, applied
automatically) + `docker-compose.prod.yml` (named explicitly, requires real secrets). One image
(`src/Host/GymOS.WebApi/Dockerfile`, 3 stages, 374 MB), everything configured by environment
variables using the application's own key names. Seven services validated healthy: postgres, api,
plus redis / seq / minio / pgadmin / mailpit behind profiles.

`docker compose up -d` from a clean clone is the whole setup step — no `.env`, no database install,
no migration command, ending in a working sign-in as `admin@gymos.local` / `gymos-dev-admin-password`.

### Decisions and traps found during M0.X

- **Compose interpolates each file independently before merging.** A `${VAR:?}` marker in the base
  file cannot be satisfied by an overlay that supplies the value — it aborts the whole command. So
  fail-fast markers live only in `docker-compose.prod.yml`; the base uses `:-`.
- **An empty environment variable is not an absent one, and it outranks `appsettings.json`.**
  `Security__Jwt__SigningKey: ${VAR:-}` in the base blanked the public development key and the API
  refused to start. The base now declares no secrets at all. Same for `Seed__SuperAdmin__*`.
- **`.editorconfig` must be in the build context.** `Directory.Build.props` sets
  `TreatWarningsAsErrors`, and `.editorconfig` is where severities are tuned — omitting it failed the
  image build on CA1716, a rule this repo deliberately turns off.
- **A named volume inherits the ownership of the image directory it covers**, and a directory absent
  from the image is created root-owned. Both writable paths are now created in the Dockerfile before
  `USER`. The failure mode was quiet: DataProtection logged an error, fell back to in-memory keys,
  and the container still reported healthy.
- **`POSTGRES_PASSWORD` applies only at initdb.** Changing it later does nothing to an existing
  volume; the symptom is `password authentication failed`, which looks like a config error.
- **`cap_drop: ALL` breaks more than expected, and quietly.** Redis (`setpriv: setresuid failed`),
  Seq (`/seqsvr/Seq: Operation not permitted` — its binary carries `cap_net_bind_service`, and
  executing a file whose capabilities are outside the bounding set fails with EPERM), and pgAdmin
  (silently fell back from port 80 to 8080, leaving the published port pointing at nothing) all had
  to be booted to be found. Fixes: run redis and seq as their own uid, grant Seq the one capability
  it declares, state pgAdmin's port.
- **YAML anchors do not cross compose files.** pgAdmin and Mailpit were defined only in the override
  file, so they never inherited `*default-hardening` and ran with Docker's default capability set.
  The override now defines its own anchor.
- **MinIO runs as root** and is the one documented exception: its image ships a root-owned `/data`.
  Development-only stand-in for Azure Blob Storage (AD-44).
- **Production applies migrations as a separate step**, not on start-up
  (`dotnet ef migrations script --idempotent` piped to psql). This exposed a real gap: `SuperAdminSeeder`
  lacked the pending-migrations guard `PlatformIdentitySeeder` had, so it threw
  `relation "platform.users" does not exist` instead of warning. Fixed.
- `Serilog:ConsoleFormat` (`json` | `text` | unset) added so one image serves both a person reading
  `docker compose logs` and a collector reading production.
- CI gained a `docker` job: builds the image, `up -d`, smoke-tests health + login, boots every
  profile, asserts the hardening with `docker inspect`, and proves the production overlay refuses to
  render without secrets.

### Still owed, in this order

- **Write the RLS startup validation (R14) with the first tenant-scoped entity.** R14 has never run;
  a missed policy is a cross-tenant leak.
- **The Members domain is frozen** (`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`). Implementation follows it
  exactly; a change needs a decision recorded in its §20 register first. Note the guardrails in its
  §19 — seventeen things an implementation must not quietly undo, chief among them: Membership Type and
  Membership Instance stay separate, Member Numbers are never derived or reused, no age is hard-coded,
  and no medical detail is stored anywhere.
- **Before a live gym uses it:** populate the legal values (§20.1–20.3, §20.6) from counsel, settle
  merge policy (§20.9), and tell the gym owner in writing about the offline-gateway limitation (§11.10).

## AD-39 idempotency framework as built

One migration: `AddIdempotencyRecords` (`platform.idempotency_records`). No production consumer yet —
the first is invoices mark-paid (M3.3, next increment); `IdempotentCommandConventionTests` keeps the
theory that enforces this live against an in-test-assembly exemplar until then.

1. **Claim commits immediately; the handler runs inside its own transaction.** `TryClaimAsync`'s
   INSERT autocommits — no ambient transaction — so every API instance sees the claim the moment it
   lands. `IdempotencyBehavior` then opens an explicit transaction around the handler and flips the
   record to `Completed`/`FailedDeterministic` inside it, so the business effect and the stored
   outcome commit atomically. Only idempotent commands (`IIdempotentCommand`) pay for any of
   this — every other request passes straight through the behavior.
2. **What is stored is the outcome, not an HTTP response.** A replay re-enters `WriteResults` with
   the decoded `Result`, exactly as a fresh execution would, so the two paths cannot drift in how a
   status or body gets built — the framework never has to know about HTTP at all.
3. **`ux_idempotency_records_claim` is raw SQL in the migration**, the same treatment M3.1 gave the
   `(plan, cycle, currency)` price index: EF's index builder cannot express this composite
   uniqueness either.
4. **`TryClaimAsync` bounds its retry at three attempts.** The INSERT can lose to a concurrent claim
   whose owner then releases it (a transient failure) before the losing call's fallback lookup
   runs — the row is simply gone, not "someone else holds it" — so the call loops back and retries
   the INSERT instead of surfacing a false conflict. Past three attempts that stops looking like the
   race and starts looking like a bug, so it throws.
5. **`IdempotencyRecordMaintainer`** purges expired records at startup — an interim owner of
   retention, same shape as `AuditLogPartitionMaintainer` (M1.3). Both fold into the M3.4 retention
   job once it exists (see Known open items).

## M3.4 as built

**Branch `m3.4-jobs`** (awaiting review). Design (approved before implementation):
`docs/superpowers/specs/2026-08-10-job-runner-design.md` — hand-rolled schedules over a cron
dependency, auto-PastDue on an overdue current-period invoice, trials auto-convert-and-bill.

- **The job runner is BuildingBlocks machinery**: `IScheduledJob` + `JobSchedule`
  (`Every`/`DailyAtUtc`, deliberately not cron — Hangfire is banned pending an ADR, and no job
  needs more) + `JobHost` (`BackgroundService`, one loop per job, fresh scope per tick, failures
  logged and the schedule advances). Cross-instance exclusion is `IJobLock` →
  `PostgresJobLock` (session advisory lock on the scope's own connection; a crashed holder's lock
  dies with its session — integration-tested both ways). `Jobs:Enabled=false` turns the host off
  for hand-driven tests. New package: `Microsoft.Extensions.Hosting.Abstractions` (approved
  `Microsoft.Extensions.*` family — no dependency-policy change needed).
- **`RetentionJob` (daily 03:10 UTC)** — the standing instruction executed: the recurring halves
  of `AuditLogPartitionMaintainer` + `IdempotencyRecordMaintainer` on a real schedule; the startup
  calls stay for first boot. Partition *detachment* still waits on the counsel retention window.
- **`RenewalJob` (15 min)** — candidates from the `(status, current_period_end_utc)` index, then
  one `SubscriptionRenewalService` step per subscription **in its own scope/transaction**. The
  four questions in order: expired trial converts-and-bills (unless cancellation is scheduled —
  converting would bill a customer on their way out); scheduled cancellation finalizes at period
  end (`CancelNow`, reason kept); auto-renew off expires (`Expired`, not `Canceled` — churn counts
  decisions); otherwise apply a due pending change, `Renew()` from where the period *ended* (no
  billing-date drift), and issue the renewal invoice at the **copied** price (the snapshot rule).
  PastDue renews too — service is never withheld by billing state. Every branch consumes the
  condition that selected it, so re-running a crashed sweep is a no-op (tested).
- **`DisableAutoRenew()`** added to the aggregate: §2.3's `auto_renew` column and the expiry
  branch are one policy, and nothing could turn the flag off short of cancelling. No endpoint yet
  (AD-45's lever); no event, the `CancelPendingPlanChange` reasoning.
- **`InvoiceOverdueJob` (hourly)** — `Issued` past due → `MarkOverdue(now)` (aggregate-guarded
  against a wrong-clock run; raises `invoice.became_overdue`, the future dunning hook) and, for a
  **current-period** invoice on an **Active** subscription, `MarkPastDue()` in the same
  transaction. Old invoices mark only themselves. Transitions added deliberately:
  `Issued → Overdue`, `Overdue → Paid | Void`; an overdue invoice is *open*, so mark-paid and void
  accept it (`IsOpen`), and the M3.3 settled-period rule closes the loop with no operator: pay
  everything open in the period and the subscription comes back.
- **The read models' one-definition overdue block** now includes the stored status: filter,
  projections, and the domain's `IsOverdue` all answer `Overdue ∨ (Issued ∧ past due)` — an Issued
  invoice is honestly overdue in the window before the sweep's next pass.
- The past-tense event checker learned `became` (`invoice.became_overdue`).

## M3.3 as built

**Branch `m3.3-invoices`** (not merged; `main` is untouched). All seven tasks done, each its own
commit. Design: `docs/superpowers/specs/2026-08-08-invoices-design.md`. Plan:
`docs/superpowers/plans/2026-08-08-invoices.md`. Business rules as built:
**`docs/BusinessRules/Invoices.md`**. Per the increment protocol this whole branch is
**awaiting review before merge**.

| Task | State |
|---|---|
| 1 — Invoice domain (aggregate, transition table, rules, events) | ✅ `dbae5dc` |
| 2 — proration calculator + scheduled plan change | ✅ `3936aba` |
| 3 — persistence (EF config, `AddInvoices` migration, repository, number generator) | ✅ `615fdd1` |
| 4 — issuing on assignment / trial conversion / plan change, net-sign policy | ✅ `1f46070` |
| 5 — mark-paid (first production `IIdempotentCommand`), void, queries | ✅ `9ab1436` |
| 6 — infrastructure queries + endpoints + OpenAPI snapshot (5 new operations only) | ✅ `f7ae208` |
| 7 — docs, exemplar cleanup, full verification, live idempotent-replay proof | ✅ (this commit) |

**Test counts after the review fixes** — every project run complete, no `Aborted`, 0 build warnings:
SharedKernel 126 · BuildingBlocks 42 · Architecture **133** · Domain **326** · Application **181** ·
Infrastructure **67** · Integration **174** = **1049** (was 997 after task 2). Architecture went down
by two versus task 2: removing the two seeded idempotency exemplars deletes four theory rows while
the real `MarkInvoicePaidCommand` adds two.

**Unrelated latent failure fixed in passing:** `IdempotencyPipelineTests` pinned a `FakeClock` at
2026-08-07 while the store compares expiry against PostgreSQL's real `now()`; with 72h retention,
every stored outcome was born expired once real time passed 2026-08-10 12:00 UTC and all four tests
failed on any checkout. They now use `SystemClock`, like `IdempotencyStoreTests` always did.

### Decisions taken in tasks 3–7

- **The number function guards its own creation race.** Two first-of-the-year issuers can both pass
  the `pg_sequences` existence check and race `CREATE SEQUENCE`; `IF NOT EXISTS` does not close that
  window (PostgreSQL can surface it as a catalog unique violation). The function catches
  `duplicate_object`/`unique_violation` and proceeds — the loser uses the winner's sequence. The
  persistence test runs its 8 parallel callers **cold** so the guard itself is exercised.
- **"Same plan" is keyed on plan *and terms*, not plan id alone** — plan-id-only keying would forbid
  a same-plan cycle change (monthly → yearly), the adjustment
  `AGymAlreadyOnAWithdrawnPlan_CanChangeItsTerms` protects. **Post-review:** re-sending the current
  terms is an idempotent **no-op success** (the route is a PUT; retry-after-timeout must succeed),
  not the `change_to_same_plan` refusal originally built — that error is gone. With a pending change
  it is still the cancellation gesture.
- **`InvoiceIssuer` only Adds; the calling handler saves** — invoice and subscription change commit in
  one transaction (`SuperAdminGuard` precedent). `SubscriptionStateCommandHandlers.MutateAsync` gained
  an async-mutate overload so trial conversion can load the plan and issue inside the same flow.
- **Invoice line display order is a projection decision**: `invoice_lines` deliberately has no
  insertion-order column (lines are write-once), so the detail orders charges before credits
  (amount descending). The plan's "ordered by creation" was unimplementable without a schema addition
  nobody needs.
- **`InvoiceLine` is exempt from the concurrency-token rule**: write-once at issue, and the only
  mutations that exist (mark-paid, void) touch the invoice row, whose `xmin` guards them.
- **PastDue interplay is scoped to the current period**: paying an invoice issued on or after
  `CurrentPeriodStartUtc` flips a PastDue subscription back to Active in the same save; paying an
  older invoice does not.
- **The gym invoice tab returns an empty list for an unknown gym**, not 404 — "no invoices" is the
  truthful answer either way, and gym existence is the gym endpoints' question.
- **OpenAPI snapshot diff verified semantically**: exactly five operations added (`ListInvoices`,
  `GetInvoice`, `MarkInvoicePaid`, `VoidInvoice`, `ListGymInvoices`), none removed — the raw diff is
  large only because path insertion re-orders the document.
- The AD-39 HTTP tests deferred from that increment landed in `InvoiceApiTests.MarkPaidIsIdempotentEndToEnd`:
  missing key → 400 `idempotency.key_required`; replay → byte-identical body + `Idempotency-Replayed: true`;
  reused key with different body → 422 `idempotency.key_reuse_mismatch`; deterministic failure
  (`invoices.already_paid`) stored and replayed under its own key.

### Post-plan review fixes (workflow review, 10 confirmed findings, all fixed)

A high-effort adversarial review before merge confirmed ten findings — six of them real money bugs
concentrated in the plan-change path. All are fixed with a pinning test each:

- **Plan changes are gated by state before proration runs** (the decision tree in
  `docs/BusinessRules/Invoices.md`): Trialing → change now, bill nothing (was: prorated a period the
  gym never paid for, then double-billed at conversion); period already ended → change now, bill
  nothing (was: parked on a pending change dated in the past that no job applies); PastDue →
  scheduled at period end (was: credited unpaid time); cross-cycle → scheduled at period end (was:
  allocated e.g. an annual price over a monthly period, a ~12× overcharge).
- **`GymSubscription.ChangePlan` clears `PendingChange`** — an applied change supersedes a scheduled
  one, or the renewal job would revert a paid upgrade.
- **Same-terms re-assign is an idempotent no-op success**; `subscriptions.change_to_same_plan`
  removed (PUT retry safety, restoring main's behavior).
- **PastDue recovery requires the period to be *settled***: mark-paid flips the subscription only
  when no other open invoice of the current period remains
  (`IInvoiceRepository.AnyOtherOpenForSubscriptionAsync` — `InvoiceStatusTransitions.IsOpen`'s first
  consumer).
- **`ListForGymAsync` pages until complete** (was: silently truncated at 200), **invoice search
  escapes LIKE metacharacters**, **the status filter validates against enum names** (Enum.TryParse
  accepts any integer string), and the **overdue predicate has one definition** in `InvoiceQueries`.

### Decisions taken in tasks 1–2

- **Tax rounds to the currency's minor units**, via `Subtotal.Multiply(rate, RoundingPolicy.HalfEven)` —
  not the plan's `Math.Round(…, 2, …)`, which truncates KWD (3 minor units) and inflates JPY (0). Same
  answer at today's 0% rate; both currencies are pinned by tests. **Task 3's `tax_rate numeric(5,4)` column
  is unaffected**, but the amount columns must keep `MoneyConventions`' `numeric(14,4)`.
- **`Draft` and `Overdue` are declared but unreachable in both directions**, asserted by the 5×5 matrix.
  Draft waits on manual invoicing, Overdue on the M3.4 job; until then overdue is derived
  (`Invoice.IsOverdue`), the M3.2 trial-expiry precedent. Whoever gives either a producer adds its
  transitions deliberately.
- **A negative invoice total throws.** The design routes a net credit to a scheduled plan change, so a
  negative total is always a caller defect — there is no credit-note document to carry one.
- **Line quantity must be 1**, refused with a message naming the constraint. Forces the first producer of
  quantity > 1 to add the multiply-with-a-rounding-policy path rather than inherit `Amount = UnitPrice`.
- **`Proration.Compute` takes both day counts from one calculation**, which is what makes the net's sign
  stable — and the sign is the entire upgrade/downgrade policy. Returns null when no days remain; clamps
  `daysUsed` so a backdated request cannot produce a negative ratio (which `Allocate` refuses).
- **The event-name worry written into task 2 was unfounded.** `EventNamesUseThePastTense` requires *any*
  underscore-separated word to be past tense, not every one, and `cancellation_scheduled` was already the
  precedent — so `gymos.platform.subscription.plan_change_scheduled` passes unchanged.
- **`CancelPendingPlanChange` raises no event**, unlike `WithdrawCancellation`: nothing acts on a scheduled
  change before the renewal job applies it, so there is no consumer to inform.
- **`PendingPlanChange` is exempt from the concurrency-token rule** — EF reports owned types from
  `GetEntityTypes()`, and it lives in the subscription's row, which its `xmin` already guards.
- **No FK on `pending_plan_id` / `pending_plan_price_id`** — EF cannot express one from inside an owned
  type without raw SQL. Low risk while plans are undeletable (M3.1 makes `DELETE` a 405); revisit if that
  ever changes.
- Extra domain guards added beyond the plan, each with a test: empty gym/subscription id, blank invoice
  number, due-before-issued, negative tax rate, blank or over-long line description
  (`InvoiceLine.MaximumDescriptionLength = 500` — **task 3's EF config should reuse that constant**).
- Test files are **flat** in `tests/GymOS.Platform.Domain.UnitTests/`, matching that project; the plan's
  `Invoices/` and `Subscriptions/` subfolders do not exist and should not be created.

### Environment notes from this session

- `dotnet ef migrations add` worked with the dev stack already up (`gymos-postgres` + `gymos-api`
  containers healthy); no host `dotnet run` was needed and nothing locked the build output.
- **The test host did not crash once** across ~10 full-project runs, including the 155-test integration
  suite. The `0x80131506` warning below still stands, but it did not reproduce today.
- **PowerShell here-strings (`@'…'@`) break `git commit -m` when the message contains double quotes** —
  the argument terminates early and git reads the rest as pathspecs. Write the message to a file and use
  `git commit -F` for anything with quotes in it.

## Standing rules (non-negotiable, enforced)

- **Architecture is frozen.** Deviations need a superseding ADR in
  `GymOS-Specification/ADR-CHANGELOG.md` *before* implementation (ADR-50/52 are the
  templates). Do not silently modify decisions.
- **Increment protocol:** explain design → implement → verify (build, tests, live run)
  → summarize → **stop for review**. Never jump ahead.
- **Dependency policy:** `docs/Engineering/DEPENDENCY_POLICY.md` and
  `ForbiddenPackagesTests` change **in the same commit**. Allowlist, not blocklist.
- **Golden Rule** (`GymOS-Specification/PRODUCT-PRINCIPLES.md`): every feature must
  reduce work for the gym owner; trim scope per module before implementing.
- House patterns already established: `IClock` only (BCL clocks are analyzer-banned);
  time flows inward as parameters; business failures are `Result`, invariant breaches
  are `DomainException`; events raised in aggregates only; source-generated
  `LoggerMessage` for logging; migrations never hand-edited (generated-code editorconfig).

## Repo map (ADR-52) — org: github.com/GymOS-Management

GymOS-Backend (this repo — THE .NET modular monolith, active) · GymOS-Specification
(frozen docs) · GymOS-Platform (Super Admin Next.js app, active, `v0.1.0`) · GymOS-Admin
(gym staff app) · GymOS-Mobile · GymOS-Gateway · GymOS-Website.
Note the trap: `GymOS.Platform.*` *projects in this repo* = platform-scope modules,
unrelated to the GymOS-Platform frontend repo.

## Fresh-machine setup

Just to run it — nothing but Docker required:

```
git clone https://github.com/GymOS-Management/GymOS-Backend
cd GymOS-Backend
docker compose up -d               # PostgreSQL + API, migrated and seeded, :5080
                                   # sign in: admin@gymos.local / gymos-dev-admin-password
```

To work on it — API on the host, database in a container:

```
dotnet tool restore                # pinned dotnet-ef
docker compose up -d postgres      # PostgreSQL 17 only
dotnet test GymOS.Backend.slnx     # expect 889 green (needs Docker) — see the test-host warning below

# Optional: an account to sign in with. Without it the seeder warns and creates nothing.
dotnet user-secrets set "Seed:SuperAdmin:Email" "you@example.com"    --project src/Host/GymOS.WebApi
dotnet user-secrets set "Seed:SuperAdmin:Password" "<12+ chars>"     --project src/Host/GymOS.WebApi

dotnet run --project src/Host/GymOS.WebApi   # migrates + seeds in Development; :5080/health/ready
```

The JWT signing key has a **development-only** value in `appsettings.Development.json`
(public, in version control, loaded only when `ASPNETCORE_ENVIRONMENT=Development`) —
the same treatment the dev database password already gets. Every other environment must
supply `Security:Jwt:SigningKey`; startup fails fast if it is missing or under 32 bytes.

**Port 5432 collision:** if a native PostgreSQL is installed locally it competes with the
container for 5432 and wins, producing `password authentication failed for user "gymos"`.
Set `POSTGRES_PORT=5433` in `.env` (the containerised API is unaffected — it reaches the
database over the compose network), or stop the local service.

Environment facts: .NET 10 SDK pinned via `global.json` (ADR-50); solution is
`GymOS.Backend.slnx`; local dev DB credentials live in `appsettings.Development.json`
matching `docker-compose.override.yml` (dev-only, deliberately public). The postgres volume
was renamed `gymos-pgdata` → `gymos-postgres-data` in M0.X, so a pre-M0.X local database is
still on disk but no longer mounted — see DOCKER.md troubleshooting.

## Known open items / honest notes

- **The dev overlay publishes ports on `0.0.0.0`**, so a running dev stack — with its
  deliberately public admin credentials — is reachable from the local network, not just the
  developer's machine. Harmless against an empty dev database; an exposure the moment a dev
  stack holds data that matters. Fix when convenient: bind to loopback in
  `docker-compose.override.yml` (`"127.0.0.1:5080:8080"`, and likewise postgres/pgadmin/seq/
  minio/mailpit). Decided 2026-08-06 to defer.

- Outbox **processor** (delivery to handlers with SKIP LOCKED claiming) not built yet —
  capture side only; lands with the first event handler need.
- **`platform.audit_logs` is partitioned** (monthly range on `created_at_utc`, migration
  `PartitionAuditLogs`). Future partitions are created by `platform.ensure_audit_log_partitions`,
  called at startup by `AuditLogPartitionMaintainer`; move that call onto the retention job's
  schedule when the job runner lands, since creating upcoming partitions and detaching expired
  ones are two halves of one policy. A DEFAULT partition means an audit write can never fail;
  rows that land there must be pruned with `DELETE` rather than `DETACH`.
- **`IdempotencyRecordMaintainer` (AD-39) purges expired `platform.idempotency_records` at
  startup** — the same interim shape as `AuditLogPartitionMaintainer` above. Both are startup-time
  stand-ins for a real schedule; fold both into the M3.4 retention job when the job runner lands
  rather than growing two separate startup sweeps.
- **AD-39 key scope is (tenant, operation, key) — caller-chosen keys are cross-user**: two users
  picking the same key for the same operation collide (spurious 422, or a replay of the other
  caller's response when the hashes match). Per-user scoping would need a superseding ADR;
  mitigated by the API_STANDARDS §10 guidance that keys SHOULD be UUIDs.
- **Permission resolution costs one query per authenticated request** (memoized per request).
  Deliberate: it buys immediate revocation. The cache-with-invalidation version belongs with
  the AD-45 settings/cache work, and should not be added before it is measured.
- Outbox **processor** (delivery to handlers with SKIP LOCKED claiming) still not built —
  capture side only; lands with the first event handler need.
- `POST /users/{id}/reset-password` (§3.6) is **not** implemented: it sends a time-boxed reset
  link, so it needs email delivery. Invited accounts therefore cannot activate themselves yet —
  the only way to give an account a password today is the `Seed:SuperAdmin` path. This is the
  first thing to build when email lands.
- Role **editing** endpoints do not exist and should not: seeded platform-role grants are
  reconciled to the code at startup. Editable roles are a tenant-scope feature.
- `Asp.Versioning` reports API versions but only v1 exists; the deprecation registry and
  `/api/versions` (AD-37 §6.2) arrive when something is actually deprecated.
- `Argon2PasswordHasher` substitutes a fixed one-byte value for an empty password because
  Konscious throws on zero-length input, which an empty password field would otherwise
  turn into a 500 on an unauthenticated endpoint. Harmless: no storable password is that
  short, so no account can hold the resulting hash.
- EF warning "No IEntityTypeConfiguration found in GymOS.Platform.Infrastructure" is
  gone since M1.1 (configurations now exist).
