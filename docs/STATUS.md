# GymOS — Implementation Status & Continuation Notes

**Last updated:** 2026-08-04 · after M1.3 (uncommitted working tree — M1.2 and M1.3 both unstaged)
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
| **M1.3 — permission pipeline, users/roles endpoints, audit interceptor, OpenAPI diff gate** | ✅ complete — **220 tests green**, verified live |
| **M2 — gym management** (gyms CRUD, suspend/delete, branding, gym detail) | ⬅ **NEXT** |
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

Adds one migration (`AddAuditLog`) — `platform.audit_logs`.

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
dotnet test GymOS.Backend.slnx     # expect 220 green (needs Docker for integration tests)

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
- **`platform.audit_logs` is not partitioned yet.** ARCHITECTURE.md §2.6 specifies monthly
  range partitions; the table is created as an ordinary one because partition DDL cannot be
  expressed through generated EF migrations, and partitioning is only meaningful alongside the
  retention/purge job that detaches old partitions. Converting later needs a data migration —
  cheap now (no production data), so **do it with the retention job and before go-live**, not
  after.
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
