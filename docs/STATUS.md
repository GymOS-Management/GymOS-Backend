# GymOS — Implementation Status & Continuation Notes

**Last updated:** 2026-08-05 · after M2 (gym management) and `Money` (AD-42)
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
| M3+ — per approved roadmap (subscriptions, feature flags, dashboard, …) | later |

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

Full counts per project, for checking a run was complete: SharedKernel 126 · BuildingBlocks 14 ·
Architecture 70 · Domain 163 · Application 103 · Infrastructure 67 · Integration 102 = **645**.

### Still owed before M3 / Members, in this order

- **Write the RLS startup validation (R14) with the first tenant-scoped entity.** R14 has never run;
  a missed policy is a cross-tenant leak.
- **The Members domain is frozen** (`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`). Implementation follows it
  exactly; a change needs a decision recorded in its §20 register first. Note the guardrails in its
  §19 — seventeen things an implementation must not quietly undo, chief among them: Membership Type and
  Membership Instance stay separate, Member Numbers are never derived or reused, no age is hard-coded,
  and no medical detail is stored anywhere.
- **Before a live gym uses it:** populate the legal values (§20.1–20.3, §20.6) from counsel, settle
  merge policy (§20.9), and tell the gym owner in writing about the offline-gateway limitation (§11.10).

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
(frozen docs) · GymOS-Platform (Super Admin Next.js app, not started) · GymOS-Admin
(gym staff app) · GymOS-Mobile · GymOS-Gateway · GymOS-Website.
Note the trap: `GymOS.Platform.*` *projects in this repo* = platform-scope modules,
unrelated to the GymOS-Platform frontend repo.

## Fresh-machine setup

```
git clone https://github.com/GymOS-Management/GymOS-Backend
cd GymOS-Backend
dotnet tool restore                # pinned dotnet-ef
docker compose up -d --wait        # PostgreSQL 17
dotnet test GymOS.Backend.slnx     # expect 645 green (needs Docker) — see the test-host warning below

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
Stop the local service, or publish the container on another port and override
`ConnectionStrings__Database`.

Environment facts: .NET 10 SDK pinned via `global.json` (ADR-50); solution is
`GymOS.Backend.slnx`; local dev DB credentials live in `appsettings.Development.json`
matching `docker-compose.yml` (dev-only).

## Known open items / honest notes

- Outbox **processor** (delivery to handlers with SKIP LOCKED claiming) not built yet —
  capture side only; lands with the first event handler need.
- **`platform.audit_logs` is partitioned** (monthly range on `created_at_utc`, migration
  `PartitionAuditLogs`). Future partitions are created by `platform.ensure_audit_log_partitions`,
  called at startup by `AuditLogPartitionMaintainer`; move that call onto the retention job's
  schedule when the job runner lands, since creating upcoming partitions and detaching expired
  ones are two halves of one policy. A DEFAULT partition means an audit write can never fail;
  rows that land there must be pruned with `DELETE` rather than `DETACH`.
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
