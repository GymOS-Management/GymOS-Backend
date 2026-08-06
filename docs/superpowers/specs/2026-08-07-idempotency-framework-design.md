# AD-39 Idempotency Framework — Design

**Date:** 2026-08-07 · **Increment:** 1 of 2 toward M3.3 (invoices follow as first consumer)
**Authority:** `GymOS-Specification/ARCHITECTURE-ADDENDUM.md` §1 (AD-39). This document implements
that frozen decision; it changes nothing in it. Where AD-39 leaves an implementation choice open,
the choice and its reasoning are recorded here.

## Why now

M3.3 (invoices, mark-paid, void) is blocked on this framework: a retried "mark invoice paid" must
not pay twice, and AD-39 declares idempotency to be platform infrastructure, not per-feature
discipline. Like `Money` before M3.1, the framework is built while nothing depends on it — the
only time its shape is free to change — and reviewed standalone.

## Scope

**In:** the API-command boundary — marker interface, PostgreSQL claim table + migration, pipeline
behavior implementing AD-39's full decision table, host plumbing (header, replay, problem codes),
startup purge maintainer, unit + integration tests.

**Out (deliberately):**
- Event-handler / gateway / webhook entry points — same table by design, but their consumers
  (outbox processor, gateway agent, webhook inbox) do not exist yet. Nothing speculative ships.
- Retrofitting existing commands. Every current create is naturally guarded by a unique
  constraint (gym slug, user email, one-live-subscription partial index); none qualifies.
  The first `IIdempotentCommand` is invoices' mark-paid, next increment.
- HTTP-level end-to-end tests — they need a real endpoint; they arrive with the first consumer,
  exactly as `Money`'s wire-format tests waited for M3.1.

## 1. Contract & placement

```csharp
// GymOS.BuildingBlocks.Application — beside the existing pipeline abstractions
public interface IIdempotentCommand
{
    string OperationKey { get; }      // stable, e.g. "billing.mark-invoice-paid"
    TimeSpan RetentionPeriod { get; } // financial ops declare longer (72h+); default 24h
    bool KeyRequired { get; }         // true → request without Idempotency-Key rejected 400
}
```

A scoped `IIdempotencyKeyAccessor` carries the `Idempotency-Key` header value from the host into
the pipeline — the same inward-flow pattern as `ICurrentUser`. The Application layer never sees
HTTP types.

## 2. Storage & claiming

Migration `AddIdempotencyRecords` creates `platform.idempotency_records`:

| column | notes |
|---|---|
| `tenant_id uuid NULL` | every current command is platform-scope; nullable until tenant-scoped consumers exist |
| `operation_key varchar` | from the command |
| `idempotency_key varchar` | from the header |
| `request_hash char(64)` | SHA-256 hex, see §4 |
| `status smallint` | InProgress=1, Completed=2, FailedDeterministic=3 |
| `response_status smallint` | outcome kind for host mapping |
| `response_body jsonb` | serialized handler outcome (DTO or business-error), see §4 |
| `lease_expires_at timestamptz` | crash takeover, see below |
| `created_at / expires_at timestamptz` | `expires_at = created_at + RetentionPeriod` |

**Unique:** `(COALESCE(tenant_id,'00000000-0000-0000-0000-000000000000'), operation_key,
idempotency_key)` — expression index in raw SQL in the migration (same technique and same
documented reason as plan-price uniqueness: EF cannot express it), asserted by a test reading
`pg_indexes`. Plain-column uniqueness would let two NULL-tenant rows coexist, which PostgreSQL
permits and AD-39's semantics forbid.

**Claiming** is AD-39's single atomic `INSERT … ON CONFLICT DO NOTHING`, and the claim **commits
immediately**, outside the handler's transaction. That is what makes a concurrent duplicate on any
API instance observe `InProgress` and receive its 409 — the unique constraint is the lock; no
Redis, no lock manager (the platform's standing DB-as-coordinator choice). Crash safety comes from
the lease: a claim whose `lease_expires_at` has passed and whose `request_hash` matches is taken
over with a conditional `UPDATE`, because the crashed attempt's effects never committed (§3).
Lease duration is configuration (`Idempotency:LeaseDuration`, default 60 s — comfortably above any
handler's runtime, far below a user's retry patience).

## 3. Transaction mechanics — the one convention this extends

Today every handler ends with a single `IUnitOfWork.SaveChangesAsync`; that save *is* the
transaction. AD-39's guarantee — "the record flips to `Completed` with the stored response **in
the same transaction** as the effect" — cannot ride on that convention, because the response
outcome exists only after the handler returns, i.e. after its save.

For idempotent commands **only**, the pipeline behavior therefore opens an explicit EF Core
transaction:

```
claim (already committed, separate)
→ BeginTransaction
→ handler executes; its SaveChangesAsync joins the ambient transaction
→ behavior stamps the record Completed + serialized outcome; SaveChangesAsync
→ Commit
```

Effect, outbox rows, audit rows, and the `Completed` record commit or roll back as one — the
transactional-outbox reasoning applied to requests. Non-idempotent commands keep today's
convention untouched. On any exception the behavior rolls back and **deletes its claim** (transient
failures are never stored), so a retry re-executes — which is the entire point of the key.

## 4. Decision table, replay, and hashing

The full AD-39 table is implemented literally; each row is one named unit test:

| Stored state | Same hash? | Behavior |
|---|---|---|
| none | — | execute; claim first |
| Completed | yes | replay stored outcome + `Idempotency-Replayed: true` |
| Completed | no | **422** `idempotency.key_reuse_mismatch` |
| InProgress, lease valid | either | **409** + `Retry-After` (`idempotency.operation_in_progress`) |
| InProgress, lease expired | yes | take over and execute |
| FailedDeterministic | yes | replay the stored business error |
| transient failure | — | never stored; claim released; retry re-executes |

`KeyRequired` without a header → **400** `idempotency.key_required`. All three problem codes are
RFC 7807 with the stable machine code in the `code` extension, per API_STANDARDS.md.

**What the record stores** *(decision)*: the **handler outcome** — success DTO or business-error
code — not raw HTTP bytes. A replay re-enters the host's normal `WriteResults` / ProblemDetails
mapping, so the wire response is byte-identical because that mapping is deterministic, and the
record write can share the handler's transaction, which a host-middleware capture could not.
Rejected alternative: storing the serialized HTTP response breaks the atomicity guarantee or
requires smuggling the transaction into middleware.

**`request_hash`** *(decision)*: SHA-256 over the **bound command object**, serialized with
deterministic System.Text.Json options (alphabetically ordered properties, invariant formatting).
Immune to client-side whitespace and key-order differences that would falsely 422 a legitimate
retry serialized by a different client build. Rejected alternative: hashing raw body bytes
(Stripe's model) needs request buffering and punishes byte-level innocuous differences.
The idempotency key itself and the caller identity are *not* part of the hash; the key is the
identity, and authorization already ran (pipeline position: Validation → Authorization →
**Idempotency** → handler, per AD-39 §1.4 — a stored response is never returned to a caller who
could not execute the operation now).

## 5. Retention & purge

`expires_at = now + RetentionPeriod`; an expired key behaves as a brand-new request (documented,
Stripe-style, per AD-39). The purge belongs to the M3.4 job runner; until it exists, a startup
maintainer (`IdempotencyRecordMaintainer`) deletes expired rows — the interim pattern
`AuditLogPartitionMaintainer` already established. **M3.4 note:** fold both maintainers into the
retention job when the runner lands; record this in STATUS.md open items during implementation.

## 6. Testing

- **Unit** (`BuildingBlocks.UnitTests` — the behavior lives in BuildingBlocks): decision-table matrix
  (one test per row), lease expiry via `FakeClock`, canonical-hash properties (order/whitespace
  invariance; different values → different hashes).
- **Integration** (Testcontainers, real dispatcher + real PostgreSQL): a test-only
  `FakeIdempotentCommand` dispatched through the production pipeline — replay verbatim outcome,
  422 on mismatched reuse, two concurrent dispatches racing to exactly one execution + one 409,
  expired-lease takeover, FailedDeterministic replay, unique-index assertion via `pg_indexes`.
- **Architecture**: `IIdempotentCommand` implementors must be commands (not queries) — one new
  convention test.

## Standing-rules compliance

- Architecture frozen: implements AD-39 as written; no deviation, so no new ADR.
- `IClock` only; lease and expiry computed from injected time; `FakeClock` in tests.
- Business failures are `Result`; the 422/409/400 idempotency outcomes are protocol-level
  rejections raised before the handler, mapped by the host, never `DomainException`.
- Dependency policy untouched — no new packages.
- Golden Rule: invisible infrastructure; its user-visible effect is that a gym owner's
  double-click never double-charges.
