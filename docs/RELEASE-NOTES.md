# GymOS Backend — Release Notes

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
