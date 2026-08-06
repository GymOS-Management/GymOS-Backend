# GymOS Backend — Release Notes

---

## M3.2 — Subscription assignment

**Suggested version:** `0.7.0`
**Status:** complete, awaiting review. Business rules: [BusinessRules/Subscriptions.md](BusinessRules/Subscriptions.md).

What each gym agreed to pay. Closes two deferrals from earlier increments and leaves M3's remaining two
parts blocked on infrastructure that still does not exist.

### The price snapshot is a copy, not a foreign key

§2.3 calls `plan_price_id` the "price snapshot FK", and **an FK cannot be the snapshot** — the same
section makes `(plan_id, billing_cycle, currency)` unique, which leaves no room for price versioning, so
changing a price *mutates that row*. A subscription that only pointed at it would be silently repriced,
the exact opposite of "renewals honor the price at assignment"; withdrawing a price would dangle the
reference. So the amount, currency, and cycle are **copied** at assignment and `plan_price_id` remains a
nullable pointer for provenance. Verified live: repricing a plan €49.99 → €79.99 leaves an existing
subscription on €49.99 while a new one takes €79.99. This honours the spec's intent; its literal
mechanism is unworkable against its own constraint.

### Added

- **`platform.gym_subscriptions`** (migration `AddGymSubscriptions`), with a **partial unique index** on
  `gym_id WHERE status IN (1,2,3)` — one live subscription per gym, every ended one kept.
- **`GymSubscription` aggregate** with a guarded lifecycle: `Trialing → Active ⇄ PastDue`, and
  `Canceled`/`Expired` terminal. Both terminal states exist because cancellation is a decision (churn)
  and expiry is a failure to collect; collapsing them would make M5's churn metric wrong.
- **`/api/v1/gyms/{gymId}/subscription`** — get, history, assign-or-change (`PUT`), cancel, resume,
  convert-trial, payment-state.
- **Permission `subscriptions.manage`**, separate from `billing.manage`: deciding what the platform sells
  and deciding what one customer pays are different authorities. Reads use `billing.read`.
- Subscriptions are **audited**.

### Behaviour worth knowing

- **Scheduled cancellation is not cancellation.** `cancelAtPeriodEnd` leaves the subscription `Active`;
  the gym paid to the end of the period. Cutting it off then would be the most expensive kind of billing
  bug. `immediately: true` is available and deliberate.
- **`PastDue` does not withhold service** — that is a gym suspension, a separate decision. A failed card
  must not end a business's day.
- **Changing plan updates the one subscription** rather than ending it, so an upgrade is never recorded
  as churn.
- **A trial is a grace on payment, not a pause on the calendar**: a monthly plan with a 14-day trial
  still renews a month in.
- **Trial conversion, renewal, and expiry are explicit operations** until the M3.4 job runner exists. A
  trial that has run out stays `Trialing` with `trialEndsAtUtc` in the past — legible rather than wrong.

### Closed deferrals

- **Gym detail now carries `subscription`** (null when the gym has none). M2 omitted it as "arrives with
  M3".
- **Plan detail and list now carry `subscribedGymCount`** — live subscriptions only. M3.1 omitted it
  because it could then only have read zero.

### Changed

- **Rule R4 now encodes its principle instead of a growing list.** A money-shaped `decimal` is allowed
  when its declaring type is a record *and* declares a currency member — "never an amount without its
  unit" — because `Money` still has no wire form. This replaced six by-name exemptions that all made the
  same argument, and four more would have been added this increment. Verified with two decoys: an
  aggregate with a `decimal MonthlyFee` is still caught (it is a class), and a record carrying an amount
  with no currency beside it is still caught. The residual risk — a domain value object written as a
  record pairing a decimal with a currency string — is documented at the rule.
- The past-tense event check learned more irregular participles, so `subscription.cancellation_withdrawn`
  passes. "Withdrawn" is past tense; renaming the event would have been the wrong fix.
- **Proration on plan change moved from M3.2 to M3.3.** It needs an invoice to land on; `Money.Allocate`
  is ready but there is nowhere to put the result. Changing plan therefore leaves the paid period alone.

### Still blocked

`invoices` (M3.3) needs the **AD-39 idempotency framework** — a retried "mark paid" must not pay twice.
Renewal and overdue jobs (M3.4) need a **job runner**, which M5's stats and the audit-retention job also
wait on. Neither should be built as a side effect of a billing feature.

### Upgrade notes

- Run `AddGymSubscriptions`. One new table; nothing existing is altered.
- No breaking API change. `subscriptions.manage` appears on SuperAdmin and BillingAdmin after one startup.
- `GymDetail` gains a nullable `subscription`, and plan responses gain `subscribedGymCount` — additive.

---

## M3.1 — Subscription plan catalogue

**Suggested version:** `0.6.0`
**Status:** complete, awaiting review. Business rules: [BusinessRules/Subscriptions.md](BusinessRules/Subscriptions.md).

The price list the rest of M3 bills against, and `Money`'s first real consumer.

**M3 is four increments, because three of its four parts have prerequisites that do not exist yet:**
subscription assignment (M3.2) needs this catalogue; invoices (M3.3) need the **AD-39 idempotency
framework**, since a retried "mark paid" must not pay twice; renewal and overdue jobs (M3.4) need a **job
runner**. Neither is built, and building either as a side effect of a billing feature would be the wrong
way to get them. `plan_features` moves to M4 with the `features` catalogue it points at.

### Added

- **`subscription_plans` and `plan_prices`** (migration `AddSubscriptionPlans`). Platform-scoped.
- **`SubscriptionPlan` aggregate** — code, name, trial, capacity limits, sort order, plus `IsActive`
  (assignable) and `IsPublic` (advertised) as the two separate questions they are.
- **`PlanPrice`** holding a `Money`: one row per (cycle, currency), so offering annual billing or a second
  currency is data entry rather than a migration. `PUT /plans/{id}/prices` is an upsert — "the annual euro
  price is €480" is one intent.
- **`BillingCycle`** whose numeric values are month counts (Monthly=1, Quarterly=3, Yearly=12), so a
  period's length is the enum value itself.
- **`/api/v1/plans`** — list, detail, create, update, set price, remove price, deactivate, activate.
  There is deliberately **no delete**: `DELETE` returns 405, and the API test asserts it.
- **Permissions `billing.read` / `billing.manage`**, granted to the seeded roles. `PlatformAdmin` gets
  read but not manage — what the platform charges is a commercial decision, not day-to-day admin.
- Plans and prices are **audited**.

### Fixed

- **A change confined to a `Money` value was silently unaudited.** `EntityEntry.Properties` omits
  complex-type members, so the audit interceptor saw an empty change set, concluded "nothing but a
  timestamp moved", and dropped the entry — meaning a plan repriced from €50.00 to €59.99 left **no record
  of who repriced it**. The interceptor now walks complex properties recursively and names fields by path
  (`Price.Amount`), which also keeps an invoice's future `Net.Amount` and `Tax.Amount` distinguishable.
  Found during the live run, pinned by an integration test.
- **The integration suite exhausted PostgreSQL's connection limit** as it grew: each test host opened a
  default 100-connection Npgsql pool against a container allowing 100 clients, surfacing as
  `53300: sorry, too many clients already` in whichever unrelated test ran when the limit was hit. Pools
  are now capped at 5 per host and the container allows 300.

### Changed

- **Rule R4 is now enforced against real money-shaped members.** Three are exempted with reasons: a domain
  event payload and two API DTOs, all serialisation boundaries where an amount travels beside an explicit
  currency code. `Money` has **no defined wire form** — `Currency` keeps its code in a private field
  behind a property that throws when unset, so System.Text.Json would quietly produce a default rather
  than fail. A `JsonConverter` belongs with the first consumer that actually needs one.
- The domain-event past-tense check learned irregular participles, so `plan.price_set` passes. "Set" is
  past tense; renaming the event to `price_changed` would have misdescribed the first-time add.
- `PlanPrice` is exempted from the concurrency-token requirement, with its reason: it is only ever written
  through `SubscriptionPlan`, whose token guards it and whose `ETag` is the one a caller can actually send.

### Upgrade notes

- Run `AddSubscriptionPlans`. Two new tables; nothing existing is touched.
- No breaking API change. `billing.read`/`billing.manage` appear on existing roles after one startup.

---

## `Money` (AD-42)

**Suggested version:** `0.5.1`
**Status:** complete, awaiting review. No migration; no consumer yet, deliberately.

Built in the window where it is free: after M2, before any table holds a monetary column. Retrofitting a
money type onto existing `decimal` columns means a migration per table and a re-reading of every figure
already stored.

### Added

- **`Currency`** — ISO 4217 code plus minor-unit count. The count is the point: JPY has none, KWD has
  three, and treating either as hundredths creates or destroys money at every rounding step.
- **`Money`** — immutable, currency-checked, rounded to minor units on construction.
  `Add`/`Subtract`/`Multiply`/`Negate`/`Abs`, comparison operators, and minor-unit conversion for
  payment providers. `Multiply` takes a **required** `RoundingPolicy` — who absorbs half a cent is a
  commercial decision, not a default.
- **`Money.Allocate(params int[] ratios)`** — largest-remainder distribution guaranteeing
  `sum(parts) == whole`. €100 three ways is 33.34/33.33/33.33, and a credit splits as the exact negation
  of the charge it reverses.
- **`CurrencyMismatchException`** — a `DomainException`, because adding euros to dollars is a defect in
  the code that did it, not a user-facing outcome.
- **`RoundingPolicy`** — `HalfEven`, `HalfUp`, `TowardZero`, `Down`, `Up`, named for the commercial
  decision rather than the BCL enum member.
- **`MoneyConventions.HasMoney`** — EF complex-type mapping to `{prefix}_amount numeric(14,4)` +
  `{prefix}_currency varchar(3)`, proven against the EF model before any entity uses it.
- **Rule R4 enforced** (`MoneyTypeTests`): a `decimal` member named like money outside `Money` fails the
  build. R4 had no subject until now. The enforcer was verified by introducing a violation and confirming
  it fails.

### Deliberately not in this release

- **`ApplyTax` / `TaxRate` / `TaxRoundingPolicy`** — named in AD-42, deferred to Payments. The
  per-line-versus-per-total rounding choice cannot be honoured by a method on a single amount, and EU VAT
  practice varies by jurisdiction (the spec itself calls it configuration pending accounting counsel).
- **Intermediate 4-decimal precision.** `Of` rounds to minor units, as specified. The `numeric(14,4)`
  column already carries the headroom, so the first producer of unrounded values — currency conversion —
  adds it without a migration.
- **`ICurrencyService` and `IExchangeRateProvider` (AD-43)** — the first conversion is platform MRR
  normalization, which arrives with analytics in M5.

---

## M2 — Gym Management

**Suggested version:** `0.5.0`
**Status:** complete, awaiting review. Business rules: [BusinessRules/Gyms.md](BusinessRules/Gyms.md).

The first business module. A gym is also a tenant, so this is the row every future gym-facing module
points at — which is why the lifecycle is a guarded transition table rather than a status column.

### Added

- **`platform.gyms` and `platform.gym_branding`** (migration `AddGyms`). Platform-scoped: a gym carries
  no `tenant_id`, because the row *is* the tenant's definition.
- **`Gym` aggregate** with a four-state lifecycle — `Provisioning → Active ⇄ Suspended`, and soft
  `Deleted` from any of them — enforced by `GymStatusTransitions` and covered as an exhaustive matrix.
  `Deleted` is terminal, as §2.2 requires.
- **`GymBranding` as a separate aggregate** (1:1). All fields optional; replaced wholesale rather than
  merged, so two editors converge on a state one of them chose.
- **`/api/v1/gyms`** — list, detail, provision, update, `activate`, `suspend`, `reactivate`, `DELETE`,
  `PUT /branding`. Deleted gyms are excluded from the list unless asked for, and still resolve on `GET`.
- **Seven gym value rules** as rule objects with stable codes: slug shape, ISO country and currency,
  IANA time zone (shape only), hex colour, hostname, owner email.
- **`X-Confirm-Action` support** (AD-6, first use). `DELETE /gyms/{id}` requires the header to carry the
  gym's slug; `ConfirmationHeaders` reads it and the handler verifies it against the loaded gym.
- **Permissions `gyms.read|create|update|suspend|delete`**, assigned to the seeded roles.
  `PlatformAdmin` holds suspend but **not** delete.
- **Gym and branding changes are audited**, including who suspended a tenant and why.

### Changed

- `SupportAgent`, `BillingAdmin`, and `ReadOnly` gain `gyms.read`; `PlatformAdmin` gains
  `gyms.read|create|update|suspend`. The startup seeder reconciles existing databases automatically.
- `contracts/openapi-v1.json` regenerated: six new paths, no change to any existing operation.
- `IdentitySeedingTests` compares SupportAgent's grants against `SystemRoles.Definitions` instead of a
  hard-coded count, so a deliberate grant change no longer reads as a failure.

### Fixed

- **`Gym.Provision` validated the owner's email before normalizing it**, so an address pasted with a
  trailing space was rejected as malformed. Both `Provision` and `UpdateDetails` now normalize first.

### Deliberately not in this release

Subscription, resolved feature flags, aggregate stats, and the device summary are named on the gym detail
view in §3.3 and are absent here: each belongs to a module that does not exist yet (M3, M4, M5, M6).
Adding a field later is compatible within v1; a field that always reads zero teaches operators to
distrust the screen.

### Upgrade notes

- Run the `AddGyms` migration. It creates two new tables and touches nothing existing.
- No breaking API change. New permissions appear on existing roles after one startup.

---

## Pre-M2 — Engineering Hardening

**Suggested version:** `0.4.0`
**Status:** complete. Full assessment in [M2_READINESS.md](M2_READINESS.md).

No business module. This milestone finalized the API contract, the concurrency model, and the
engineering standards *before* the first client is generated — because every change here is free today
and breaking afterwards.

### Added

- **Optimistic concurrency**: `rowVersion` on mutable resources, strong `ETag`, `If-Match` required on
  `PUT`, `409` with `retryable: false` on a stale version, `400` on a malformed validator, `428` when
  the precondition is absent. Backed by PostgreSQL `xmin` rather than a maintained column.
- **Business rule framework**: `IBusinessRule`, `BusinessRules.Check`/`Validate`,
  `BusinessRuleViolationException`, and `BusinessRuleAssert` with a code-convention assertion.
- **`docs/BusinessRules/`** — 11 module documents (Members … Reports), each with Purpose, Entities,
  Business Rules, Invariants, Future Rules, Dependencies, and Open Questions.
- **`docs/Engineering/API_STANDARDS.md`** and **`docs/ENGINEERING_PLAYBOOK.md`**.
- **`DomainEventConventionTests`** enforcing R9 across every production assembly: naming, versioning,
  per-instance identity, platform-wide uniqueness, past tense, immutability.
- **`ConcurrencyModelTests`** asserting every mutable entity has a concurrency token, or a reasoned
  exemption — the gap a behavioral test cannot see.

### Changed

- **Write responses return the resource.** `POST` → `201` + `Location` + resource; `PUT` and action
  endpoints → `200` + resource; `DELETE` and resource-less actions → `204`. Previously all `204`.
  Breaking, and made now precisely because it is free before the first generated client.
- `UserDetail` carries `rowVersion` (additive).
- `DomainException` is unsealed so `BusinessRuleViolationException` derives from it — one exception
  strategy, and every existing `catch (DomainException)` keeps working.
- `User.Create` validates email through the rule framework rather than an inline throw.
- `PermissionAuthorizationHandler` uses the request's cancellation token.

### Fixed

- **A write that changed only child rows did not advance the aggregate's version**, so two
  administrators editing the same role set both succeeded — last-write-wins on the operation most
  likely to be edited concurrently. `MarkAggregateChanged` closes it, and the rule is documented for
  every future aggregate.
- An audit entry whose only change was the row timestamp is no longer written; a diff saying "the
  timestamp moved" tells a reader nothing.
- The past-tense event-name check rejected `user.locked_out` — the test was wrong about English, not
  the event.

### Known Limitations

Carried from M1: no outbox processor, no retention job, no account activation, and reverse-proxy
configuration untested by any automated test. New to note: **`Money` (AD-42) is not built** and must be
before anything touches currency, and **RLS (R14) has never run** — write its startup validation with
the first tenant-scoped entity.

### Testing Summary

**337 tests** (up from 256): SharedKernel 30, Domain 42, BuildingBlocks 14, Application 66,
Architecture 46, Infrastructure 62, Integration 77. All gates green. Write convention and concurrency
verified live against PostgreSQL 17: `201`+`Location`+`ETag`, `428` without `If-Match`, `200` with a
fresh version, `409` with `retryable: false` on a stale one, `400` on a weak validator.

---

## M1 — Identity, Authentication & Access Control

**Milestones:** M1.1 (identity domain) · M1.2 (auth services & endpoints) · M1.3 (permission
pipeline, admin surface, audit trail)
**Suggested version:** `0.3.0`
**Target framework:** .NET 10 LTS (ADR-50) · PostgreSQL 17
**Status:** feature-complete against the approved architecture, with the limitations below.

M1 is the security foundation the roadmap puts first (ARCHITECTURE.md §7): nothing else can be
built until there is a way to authenticate, a way to authorize, and a record of what was done.

### Added

**Identity domain (M1.1)**
- `User`, `Role`, `Permission`, `RolePermission`, `UserRole`, `RefreshToken` aggregates in the
  `platform` schema, with domain events raised inside the aggregates.
- Declare-and-validate startup sync reconciling the permission catalog and the five seeded
  platform roles to what the code declares; orphaned permissions are logged, never silently
  deleted.

**Authentication (M1.2)**
- Argon2id password hashing with cost from configuration, stored in **PHC string format** so
  verification reads the parameters out of each hash. Raising cost affects new passwords only.
- 15-minute JWT access tokens carrying `sub`, `email`, `security_stamp` and `perm_hash`.
- Rotating opaque refresh tokens: 32 random bytes, SHA-256 at rest, httpOnly/Secure/
  SameSite=Strict cookie scoped to `/api/v1/auth`, with chain reuse detection.
- `/api/v1/auth`: `login` (rate-limited per IP), `refresh`, `logout`, `me`, `change-password`.
- Initial SuperAdmin seeding from configuration only — creates, never resets.
- RFC 7807 problem responses across the whole API, with a stable machine code in a `code`
  extension member.

**Authorization and administration (M1.3)**
- Permission enforcement at two layers over one evaluator: on-demand endpoint policies and an
  `AuthorizationBehavior` re-checking `[RequiresPermission]` on the request type.
- Security-stamp validation on every bearer token, so suspension and password change end sessions
  immediately rather than at token expiry.
- `/api/v1/users` — list (paged, filtered, sorted), detail, invite, update, suspend, reactivate,
  set-roles. Guards against self-suspension, self-role-editing, and demoting the last SuperAdmin.
- `/api/v1/roles` and `/api/v1/audit-logs` — read-only by design.
- Audit trail: row diffs captured in the transaction of the change they describe, plus explicit
  `auth.*` entries. Opt-in per entity; redaction opt-out by name pattern.
- OpenAPI contract snapshot (`contracts/openapi-v1.json`) diffed by the test suite.
- **Monthly range partitioning of `platform.audit_logs`**, with a maintenance function creating
  future partitions ahead of need and a DEFAULT partition so an audit write can never fail.

### Changed

- `global.json` pins SDK **10.0.201** as a floor rather than 10.0.301, which matched no installed
  SDK (`rollForward: latestFeature` only moves up).
- Dependency policy allowlists `Microsoft.IdentityModel.*` and `System.IdentityModel.Tokens.Jwt`
  (MIT, adapter-restricted) — implements AD-4, changes no decision.
- Outbox rows now record the acting user (`user_id`), previously always null.
- `platform.audit_logs` primary key widened to `(id, created_at_utc)` — a PostgreSQL requirement
  for the partition key. No API or query change.
- `UserStatus` transitions moved behind one explicit, exhaustively tested table
  (`UserStatusTransitions`); the rules were previously spread across three aggregate methods.
- The CI OpenAPI gate comment is replaced by the live gate in the test suite.

### Fixed

- **Empty password caused a 500.** Konscious rejects a zero-length password, and the sign-in
  timing-equalizer hashes whatever was posted — so `{"password":""}` faulted an unauthenticated
  endpoint.
- **Client-assigned Guid primary keys were tracked as `Modified`.** EF's default
  `ValueGeneratedOnAdd` made it read "key has a value" as "row exists", producing an UPDATE that
  matched zero rows and surfaced as `DbUpdateConcurrencyException` — an error naming the wrong
  cause. Fixed for every entity by `GymOSModelConventions.ApplyClientAssignedKeys()`.
- **Enum `ToString()` and positional records in EF projections** failed at runtime, not compile
  time (`GET /api/v1/roles` returned 500).
- **Client IP was the proxy's behind a reverse proxy**, silently collapsing the sign-in rate
  limiter into one shared bucket and making every audit row claim the same origin. Corrected by
  opt-in, topology-declared forwarded-header handling.
- **`GET /api/v1/roles/{roleId}` shipped undocumented** — caught by the new R12 gate.
- **Test isolation:** xUnit builds a fresh test-class instance per method, so a fixed
  Testcontainers database name shared state across a class and made row-counting tests
  order-dependent.
- **Developer user secrets leaked into tests** — `WebApplicationFactory` loads the host project's
  secret store, so a local `Seed:SuperAdmin` was seeded into test databases.

### Known Limitations

| Limitation | Impact | Mitigation / plan |
|---|---|---|
| **Invited accounts cannot activate themselves.** No email delivery, so no activation or reset link. | The only account that can sign in is the one created by `Seed:SuperAdmin`. Invited administrators exist and hold roles but cannot authenticate. | Fully designed in `docs/Engineering/ACCOUNT-ACTIVATION.md`; blocked on `IEmailSender`. Temporary passwords were considered and rejected. |
| **No outbox processor.** Domain events are captured but never delivered. | Events accumulate in `outbox.messages`; nothing consumes them yet. Required before any event-driven feature. | Lands with the first event handler need (AD-13). |
| **No retention/purge job.** | Audit partitions, expired refresh tokens, and (later) idempotency records are never pruned. | Partitioning is in place so retention is a `DETACH`, not a mass `DELETE`. Required before production. |
| **Permission resolution costs one query per authenticated request** (memoized per request). | Deliberate: buys immediate revocation. | Measure before caching; caching belongs with AD-45. |
| **Write endpoints return 204, not the updated resource**, and take no `rowVersion`. | Deviates from ARCHITECTURE.md §3 conventions; concurrent edits are last-write-wins. | See "Recommended before M2" in the review. Free to change now, breaking after the first generated client ships. |
| **`auth.signed_in` audit rows have a null `user_id`.** | Filtering the trail by `userId` does not return sign-ins. The user is identified by `entity_id` and `user_email`. | At sign-in the caller is not yet authenticated, so `ICurrentUser` is empty. Cosmetic; fix with an `IAuditTrail` overload if it ever matters. |
| **Rows in the audit DEFAULT partition cannot be detached by month.** | Only reachable if the app runs for longer than the partition lookahead without a restart. | Warned loudly; retention prunes DEFAULT by `DELETE`. |

### Deferred Items

Deferred by decision, each with its reason:

- **`LastActivityAtUtc` on `User`** — writing it faithfully means a row write on every
  authenticated request, contended across an administrator's concurrent requests. The question is
  already answerable from `audit_logs` (`user_id` + `created_at_utc`). A live presence indicator
  should be a throttled write or a read model, not a column on the aggregate.
- **`Locked` as a `UserStatus`** — lockout is a self-expiring time window
  (`locked_out_until_utc`), not a lifecycle state. As a status it would need a job to unlock
  accounts, memory of the state to return to, and a write on the sign-in read path. Reasoning
  recorded on `UserStatusTransitions`.
- **`Deleted` as a `UserStatus`** — not in the approved design for platform administrators (§3.6
  offers suspend, not delete) and the audit trail denormalizes `user_email` precisely so history
  survives a removed account. Would require a superseding ADR with erasure semantics worked out.
- **Rehash-on-login when Argon2 cost changes** — needs a domain method that upgrades the hash
  *without* rotating the security stamp, or it would sign every user out on their next request.
- **Absolute cap on sliding refresh lifetime** — needs a chain-root column to know when the
  session began.
- **Editable roles** — seeded platform-role grants are reconciled to code at startup, so an edit
  endpoint would be overwritten on the next deployment. Custom roles are tenant-scope.
- **Deprecation registry / `/api/versions`** (AD-37 §6.2) — nothing is deprecated yet.

### Architecture Compliance

| Decision | Status |
|---|---|
| AD-1 CQRS without MediatR/AutoMapper | ✅ in-house dispatcher; both packages banned and enforced |
| AD-2 Minimal APIs with endpoint groups | ✅ |
| AD-3 Repositories only where invariants demand | ✅ `IUserRepository`/`IRefreshTokenRepository`/`IRoleRepository`; reads are projections. **Deviation:** `IAppDbContext` is not used — EF Core is banned in the Application layer by R7, so a `DbSet<T>`-typed abstraction is impossible. Replaced by `IUnitOfWork` + repositories, same intent |
| AD-4 Short JWT + rotating refresh cookie, Argon2id | ✅ including stamp enforcement and chain reuse detection |
| AD-5 Permission-based authorization, defense in depth | ✅ both layers over one evaluator |
| AD-7 Audit via interceptor + explicit commands, append-only, monthly partitions | ✅ complete as of this review |
| AD-12 API-first contract | ✅ OpenAPI per version + committed snapshot |
| AD-13 Transactional outbox | ⚠️ capture only; processor deferred |
| AD-14 Event envelope | ✅ `user_id` now populated; `tenant_id`/`causation_depth` await their modules |
| AD-22 Module-owned permission catalog, no wildcards, deny-by-default | ✅ first term of the §6.2 formula; feature-flag and tenant terms await those modules |
| AD-37 URL-segment versioning + breaking-change gate | ✅ |
| AD-40 `IClock` only, time flows inward | ✅ analyzer-enforced; no direct clock read outside `SystemClock` |
| AD-49 R1–R17 | R1 ✅ · R2 ✅ · R6/R7 ✅ (NetArchTest) · R11 ✅ (endpoint + request gates) · R12 ✅ · R13 ✅ · R15 ✅ · R17 ✅. R3/R4/R5/R8/R9/R10/R14/R16 have no subject yet (no HTTP clients, money, idempotent commands, vendor adapters, module manifests, or tenant tables in M1) |
| ADR-50 .NET 10 | ✅ |
| ADR-52 single backend repo | ✅ |

No ADR was changed, superseded, or added. No new framework was introduced. The one documented
deviation (AD-3's `IAppDbContext`) is forced by another approved rule and preserves the intent.

### Testing Summary

| Suite | Tests | Scope |
|---|---:|---|
| `GymOS.SharedKernel.UnitTests` | 15 | Entity identity, `Result`, domain events |
| `GymOS.Platform.Domain.UnitTests` | 35 | Aggregates, lifecycle state machine (exhaustive transition matrix) |
| `GymOS.BuildingBlocks.UnitTests` | 14 | Dispatcher, clock, correlation |
| `GymOS.Platform.Application.UnitTests` | 66 | Auth and user-management handlers, permission behavior, password policy |
| `GymOS.Platform.Infrastructure.UnitTests` | 48 | Argon2id + PHC round-trip, JWT claims, refresh-token minting, audit redaction |
| `GymOS.ArchitectureTests` | 12 | Layer and package law, dependency policy |
| `GymOS.IntegrationTests` | 66 | Real HTTP + real PostgreSQL: full auth flow, reuse detection, admin surface, permission enforcement, audit capture, partition layout, endpoint conventions, contract snapshot |
| **Total** | **256** | |

Gates run on every commit: Release build with warnings-as-errors, `dotnet format
--verify-no-changes`, the full suite (Testcontainers PostgreSQL 17), the transitive banned-package
audit, and the OpenAPI snapshot diff.

Verified beyond the suite: the migration path applied against a real PostgreSQL 17 database, and
the full flow exercised live over HTTP — sign-in, refresh rotation, replay refusal, sign-out,
invite, suspend/reactivate, audit read with redaction confirmed, permission refusals, and both
self-protection guards.

**Not covered by tests:** email delivery and activation (not built), outbox delivery (not built),
retention (not built), and behavior behind a real reverse proxy (configuration-only, exercised by
neither unit nor integration tests).
