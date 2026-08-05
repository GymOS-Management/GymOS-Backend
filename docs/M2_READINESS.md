# M2 Readiness Assessment

**Milestone:** pre-M2 engineering hardening (no business modules)
**Assessed:** 2026-08-05 · after M1 (v0.3.0) plus this milestone
**Suggested version:** `0.4.0`
**Verdict:** see [Recommendation](#recommendation).

The question this document answers is narrow: **is the platform finished enough that building the
first business module will not force us to change it?** Everything below is measured against that,
not against feature completeness.

---

## Completed

### 1. Write response convention — finalized

| Method | Status | Body |
|---|---|---|
| `POST` creating | `201` + `Location` + `ETag` | the created resource |
| `POST` acting | `200` + `ETag` | the updated resource |
| `PUT` / `PATCH` | `200` + `ETag` | the updated resource |
| `DELETE`, action with no resource | `204` | none |

Previously every write answered `204`. Changed now because the change is free while no client is
generated and a breaking change afterwards. Three documented exceptions (`logout`,
`change-password`, and the token endpoints) return `204`/`200` for stated reasons.

One helper — `WriteResults` — implements the convention, so it cannot be reinvented per route.
Handlers return the resource by reading it back after the save, which also guarantees the response
matches what a following `GET` would return.

### 2. Optimistic concurrency — implemented end to end

- `rowVersion` on every mutable resource, in the body and as a strong `ETag`, backed by PostgreSQL
  `xmin` rather than a column anyone has to remember to bump.
- `If-Match` **required** on `PUT` (`428` without it), honored on action endpoints.
- `409` with `retryable: false` and explicit re-read/re-apply/retry guidance; `400` for a malformed or
  weak validator, so a client is never told to retry something that cannot succeed.
- **No last-write-wins**: a `PUT` with no precondition is refused, not treated as an overwrite.
- Documented in OpenAPI per endpoint, gated by `ConcurrencyModelTests` (every mutable entity has a
  token, or an exemption with a reason) and 11 integration tests.

**A real gap was found and closed here:** a write that changed only child rows left the aggregate's
version untouched, so concurrent role edits both succeeded. `MarkAggregateChanged` now advances the
root's version, and the rule is in API_STANDARDS.md §3 for every future aggregate.

### 3. Business rule framework — implemented, and in use

`IBusinessRule`, `BusinessRuleViolationException`, and `BusinessRules` (`Check` for aggregates,
`Validate` for handlers) in the SharedKernel. `BusinessRuleAssert` in TestingSupport, including a
naming-convention assertion a module can point its whole catalog at.

`DomainException` is now unsealed so a broken rule *is* a domain exception — one exception strategy,
and every existing `catch (DomainException)` keeps working. Adopted for a real rule
(`EmailMustBeWellFormed`) rather than left as speculative scaffolding. **No gym logic.**

### 4. Business rule documentation — 11 modules

`docs/BusinessRules/` covers Members, Memberships, Payments, Scheduling, Attendance, Inventory, POS,
Trainer, Nutrition, CRM, Reports — each with Purpose, Entities, Business Rules (with proposed codes),
Invariants, Future Rules, Dependencies, and Open Questions. Assumptions are marked `(?)`. Nothing is
implemented.

The index collects the recurring themes, which is where most of the value is: derive rather than
store, immutability where money or history is involved, price/rate snapshots, mandatory idempotency,
state machines over boolean flags, and legal questions that constrain schemas rather than features.

### 5. API standards — documented and gated

`docs/Engineering/API_STANDARDS.md` covers versioning, write responses, concurrency, errors,
validation, pagination/filtering/sorting/searching, naming, authorization, OpenAPI, idempotency, and
headers. Machine-checked where possible: R11 (permission declared), R12 (documented), R13 (contract
snapshot), plus a gate that every endpoint policy names a *declared* permission.

### 6. Domain event review — consistency enforced, nothing redesigned

`DomainEventConventionTests` now enforces R9 across every production assembly: naming
(`gymos.<module>.<aggregate>.<action>`, lower snake_case, past tense), versioning from 1, per-instance
`EventId` (the idempotency key), platform-wide name uniqueness, and immutability. Findings:

| Aspect | State |
|---|---|
| Naming | ✅ all 10 events conform |
| Versioning | ✅ all at 1, enforced ≥ 1 |
| Envelope | ✅ identity/name/version on the event; aggregate, time, user, correlation captured at save |
| Correlation | ✅ live on audit and outbox rows |
| Causation | ⚠️ column present, always 0 — needs the automation engine to mean anything (AD-30) |
| Tenant | ⚠️ column present, always null — correct: no tenant-scoped entity exists yet |
| Idempotency | ⚠️ `EventId` is issued; consumption is unbuilt because there is no processor |
| Documentation | ✅ each event's meaning is on its record |

No event was renamed. One test was corrected: it rejected `user.locked_out` as present tense, which
was the test being wrong about English, not the event being wrong.

### 7. Engineering review

Fixed: the write convention and concurrency above; the child-row version gap; the past-tense check.
Everything else scanned clean — no `async void`, no blocking `.Result`/`.Wait()`, no swallowed
exceptions, cancellation propagated on every async path (including the authorization handler, fixed
in M1.3), no direct clock read outside `SystemClock`, no layer or dependency-policy violation.

### 8. Engineering playbook

`docs/ENGINEERING_PLAYBOOK.md`: branch strategy, commit convention, PR and review checklists, ADR
process, Definition of Done, testing requirements, logging standards, naming, migration policy,
release process, versioning strategy — each with its enforcer named where one exists.

---

## Deferred

Each with the reason, because a deferral without one is just something forgotten.

| Item | Why deferred |
|---|---|
| **Outbox processor** | Needs a first consumer to define delivery semantics. Capture is correct and complete; nothing is lost by waiting. **Required before any event-driven feature.** |
| **Retention / purge job** | Needs the job runner (AD-23). Partitioning is already in place so retention is a `DETACH`, not a mass `DELETE`. **Required before production.** |
| **Idempotency pipeline behavior** | AD-39's semantics are decided and documented; the behavior lands with the first command that charges, books, or unlocks. No such command exists yet. |
| **Account activation / email** | Fully designed in `ACCOUNT-ACTIVATION.md`; blocked on `IEmailSender`. Temporary passwords were considered and rejected. |
| **`PATCH` support** | The convention is defined, but no resource yet needs partial update. Adding one later is additive. |
| **`If-None-Match` / 304** | The server has already done the query by then, so it saves bandwidth only. Revisit if a console polls. |
| **OpenAPI `example` payloads** | Hand-written examples drift from the schema silently. Revisit if the console team asks. |
| **Permission caching** | One memoized query per authenticated request buys immediate revocation. Measure before trading that away; belongs with AD-45. |
| **`LastActivityAtUtc`** | A row write per request, for a question `audit_logs` already answers. |
| **`Locked` / `Deleted` user states** | Lockout is a self-expiring window; user deletion is not in the approved design. Either would need a superseding ADR. |

---

## Known Risks

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| 1 | **No retention job.** Audit partitions and expired refresh tokens accumulate. | certain over time | Medium — storage growth, and `DEFAULT`-partition rows that cannot be detached | Partitioning done; job is a small piece of work once the runner exists. Do not go to production without it. |
| 2 | **Invited administrators cannot activate themselves.** | certain | High operationally — only the seeded account can sign in | Designed; blocked on email. Build it before the console ships. |
| 3 | **Reverse-proxy config is untested by any test.** Wrong `KnownProxies` collapses the rate limiter to one bucket. | Medium | High — one attacker throttles every admin | Off by default and fail-closed; verify the first deployment behind a real proxy manually. |
| 4 | **Audit `DEFAULT` partition** can accumulate rows if the app runs longer than the 3-month lookahead without a restart. | Low | Low — rows are safe, just not detachable by month | Warned loudly; retention prunes `DEFAULT` by `DELETE`. |
| 5 | **First tenant-scoped module will exercise RLS (R14) for the first time.** No tenant table exists yet, so the rule has never run. | Certain at M2 | Medium — a missed policy is a cross-tenant leak | Write the RLS startup validation *with* the first tenant entity, not after. |
| 6 | **Read-back on every write** adds a query per write. | Certain | Low | Deliberate: it is what returning the resource costs. Revisit only if measured. |
| 7 | **Business rule docs are assumptions**, not requirements. | Certain | Medium if treated as approved | Marked `(?)`; Open Questions must be answered with the product owner before each module. |
| 8 | **Legal questions block schema decisions** in POS (fiscal receipts), Members/Nutrition (health data), CRM (consent). | Certain | High — schema rework after data exists | Flagged per module. Get counsel's answer before designing those tables. |

---

## Remaining Technical Debt

Honest list. None of it blocks M2.

1. **`ListByUserAsync` loads all of a user's refresh tokens** to walk the rotation chain. Bounded only
   by the absent purge job. Fine at current volumes; revisit with retention.
2. **Every failed sign-in writes an audit row** — an unauthenticated write path, rate-limited but not
   free. Deliberate (the security record is the point); monitor.
3. **`auth.signed_in` audit rows have a null `user_id`** — the caller is not yet authenticated. The user
   is in `entity_id`. Cosmetic; an `IAuditTrail` overload would fix it.
4. **Two paths resolve permissions** — `SessionFactory` via `IIdentityQueries` at sign-in (no current
   user yet), the evaluator via the snapshot afterwards. Correct, but worth a comment if a third appears.
5. **`RoleQueries` projects then maps in memory** because enum `ToString()` and positional records do
   not translate. Documented at the call site; bounded data.
6. **No load or soak testing.** Nothing has run under concurrency beyond the tests.
7. **Test suite takes ~35s**, dominated by Testcontainers host boots. Acceptable; watch it.

---

## Architecture Compliance

| Decision | Status |
|---|---|
| AD-1 CQRS without MediatR/AutoMapper | ✅ enforced by package policy |
| AD-2 Minimal APIs, endpoint groups | ✅ |
| AD-3 Repositories only where invariants demand | ✅ **documented deviation:** `IAppDbContext` is impossible (R7 bans EF in Application); `IUnitOfWork` + repositories preserve the intent |
| AD-4 Short JWT + rotating refresh, Argon2id | ✅ including immediate stamp enforcement |
| AD-5 Permission-based authorization, defense in depth | ✅ both layers, plus a declared-permission gate |
| AD-7 Audit interceptor, append-only, monthly partitions | ✅ complete |
| AD-8 Read models for reporting | ⚠️ pattern established; no report exists |
| AD-10 Validation at every boundary | ⚠️ domain + rule framework done; the FluentValidation pipeline behavior lands with the first validator |
| AD-12 API-first contract | ✅ snapshot committed and gated |
| AD-13 Transactional outbox | ⚠️ capture only; processor deferred |
| AD-14 Event envelope | ✅ enforced by convention tests; tenant/causation await their modules |
| AD-22 Module-owned permissions, no wildcards, deny by default | ✅ first term of §6.2; flag and tenant terms await those modules |
| AD-37 URL-segment versioning + breaking-change gate | ✅ |
| AD-39 Idempotency | ⚠️ designed, not built — no qualifying command exists |
| AD-40 `IClock` only, time flows inward | ✅ analyzer-enforced |
| AD-41 Wall-clock scheduling, DST policy | ⚠️ platform time service exists; no scheduled entity yet |
| AD-42 `Money` value object | ✅ built after M2 (currency-checked arithmetic, explicit rounding policies, largest-remainder `Allocate`, EF complex-type mapping). `ApplyTax` deferred to Payments — see RELEASE-NOTES. |
| AD-48 Hierarchical health | ⚠️ readiness gates on DB only, as specified; contributors arrive with their subsystems |
| AD-49 R1–R17 | R1 ✅ · R2 ✅ · **R4 ✅ (new — `Money` gave it a subject)** · R6/R7 ✅ · R9 ✅ · R11 ✅ · R12 ✅ · R13 ✅ · R15 ✅ · R17 ✅. R3/R5/R8/R10/R14/R16 have no subject yet |
| ADR-50 .NET 10 · ADR-52 single backend repo | ✅ |

**No ADR was changed, superseded, or added. No framework was introduced.** Changes were confined to
implementation detail: `DomainException` unsealed, a composite primary key for partitioning, response
shapes, and the addition of tests.

---

## Testing Summary

| Suite | Tests | Scope |
|---|---:|---|
| `GymOS.SharedKernel.UnitTests` | 30 | Entity identity, `Result`, events, **business rule framework** |
| `GymOS.Platform.Domain.UnitTests` | 42 | Aggregates, lifecycle matrix, **identity rules** |
| `GymOS.BuildingBlocks.UnitTests` | 14 | Dispatcher, clock, correlation |
| `GymOS.Platform.Application.UnitTests` | 66 | Handlers, permission behavior, password policy |
| `GymOS.ArchitectureTests` | 46 | Layer/package law, **domain event conventions** |
| `GymOS.Platform.Infrastructure.UnitTests` | 62 | Argon2id/PHC, JWT, audit redaction, **concurrency model** |
| `GymOS.IntegrationTests` | 77 | Auth flows, admin surface, permissions, audit capture, partition layout, **concurrency**, endpoint conventions, contract snapshot |
| **Total** | **337** | up from 256 at v0.3.0 |

Gates on every commit: Release build with warnings-as-errors, `dotnet format --verify-no-changes`,
full suite on Testcontainers PostgreSQL 17, transitive banned-package audit, OpenAPI snapshot diff, and
`has-pending-model-changes`.

Verified beyond the suite, live against PostgreSQL 17: `201` with `Location`/`ETag`/resource, `428`
without `If-Match`, `200` with a fresh version and a new `ETag`, `409` with `retryable: false` on a
stale version, `400` on a weak validator, and partition maintenance reporting at startup.

**Not covered by tests:** email/activation, outbox delivery, retention, behavior behind a real reverse
proxy, and load.

---

## API Stability

**The v1 contract is now stable and safe to generate clients from.**

Everything known to need a breaking change has been made:

| Change | Made in |
|---|---|
| Write responses return the resource | this milestone |
| `rowVersion` on resources, `ETag`/`If-Match`, `409`/`428` | this milestone |
| Error shape, codes, pagination envelope | M1 |
| URL-segment versioning, concrete paths in the document | M1 |

`contracts/openapi-v1.json` is committed and diffed on every build. From here, additive change is
compatible within v1; anything else needs a v2 route for that endpoint (AD-37 §6.1).

**One caveat, stated plainly:** M2 will add endpoints, and if a *pattern* turns out to be wrong (say
`PATCH` semantics, or a filter convention), fixing it will be breaking. The conventions above are
therefore the ones to argue with now, not after the console is written.

---

## ADR Compliance

Reviewed against the frozen set as amended by ADR-CHANGELOG.md. **No amendment was required and none
was made.** The single deviation (AD-3's `IAppDbContext`) is forced by another approved rule, preserves
the intent, and is recorded here and in the release notes rather than resolved by an ADR — consistent
with the playbook's §5 guidance that a deviation forced by an approved rule is documented, not amended.

---

## Recommendation

The four things that had to be settled before a business module could be built are settled: the write
convention, the concurrency model, the business-rule framework, and the API standards. Each is
documented, each is enforced by a test a new module inherits for free, and each was verified against a
real database rather than only in unit tests.

Two items are **required before production** but not before M2, and both are recorded above with their
reasons: the retention job and account activation. Neither blocks building Gym Management; both would
block shipping it to a paying gym.

Before M2 implementation starts, three things are worth doing first because they are cheaper now:

1. ~~**Build `Money` (AD-42) before anything touches currency.**~~ **Done** — built immediately after
   M2, while no table held a monetary column, which is exactly the point. The EF mapping was proven
   against the model before any entity used it.
2. **Write the RLS startup validation (R14) with the first tenant-scoped entity**, not after. It has
   never run, and a missed policy is a cross-tenant leak.
3. **Answer the Open Questions in `BusinessRules/Members.md` and `Memberships.md`** with the product
   owner. Several change the entity shape, and one (health data) needs legal counsel.

**Is GymOS ready for M2?** Yes.

**The platform foundation is complete. M2 may begin.**
