# GymOS API Standards

**Status:** Enforced. Endpoint conventions are gated by `EndpointConventionTests` (R11, R12) and the
OpenAPI snapshot diff (R13); anything not machine-checkable is a review item on the PR checklist.
**Authority:** ARCHITECTURE.md §3 · ARCHITECTURE-PLATFORM-CAPABILITIES.md §6 (AD-37) · AD-12
**Scope:** every HTTP surface of GymOS-Backend, present and future.

This document is the contract the generated frontend clients are built from. It is finalized before
M2 for one reason: after the first client is generated, a change here is a breaking change.

---

## 1. Versioning

- **URL segment**: `/api/v1/…`. Explicit, cache-friendly, visible in logs.
- **The endpoint is versioned, not the platform.** A v2 route maps onto the *same* handler with a
  version-specific DTO translation at the endpoint layer. There is no platform-wide "v2 release".
- **A version is never assumed.** `AssumeDefaultVersionWhenUnspecified = false`: a request without a
  version is rejected rather than silently answered by whatever is current.
- **The published document carries concrete paths.** `AddGymOSOpenApi` rewrites
  `/api/v{version}/…` to `/api/v1/…` and drops the redundant `version` parameter, so a generated
  client has no argument for a value the document already fixes.

**Compatible within a version** (additive only): a new endpoint, a new optional request field, a new
response field, a new enum *reader* tolerance. **Breaking** (needs a new version): removing,
renaming, or retyping a field; changing a status code, a default, or the meaning of a value.

## 2. Write responses

| Method | Success | Body |
|---|---|---|
| `POST` creating a resource | `201 Created` + `Location` + `ETag` | the created resource |
| `POST` acting on a resource | `200 OK` + `ETag` | the updated resource |
| `PUT` / `PATCH` | `200 OK` + `ETag` | the updated resource |
| `DELETE` | `204 No Content` | none |
| Action with no resource to describe | `204 No Content` | none |

**Why not 204 everywhere.** A 204 forces a read-after-write to learn what happened, and the client's
local copy stays wrong until it does — worse after a write that touched a computed field, a
server-side default, or the row version. With optimistic concurrency it is worse still: the caller
needs the *new* version to make a second edit, so a 204 guarantees a follow-up GET before every
subsequent change.

**The documented exceptions**, each because there is genuinely no resource to return:

| Endpoint | Status | Why |
|---|---|---|
| `POST /auth/logout` | 204 | The session no longer exists. |
| `POST /auth/change-password` | 204 | Every session including the caller's was just revoked; returning a resource would imply the token still works. |
| `POST /auth/login`, `/auth/refresh` | 200 | Return a token pair, not a resource — no `ETag`, nothing to precondition. |

One helper implements all of this: `WriteResults`. New endpoints go through it rather than calling
`TypedResults` directly, so the convention cannot be reinvented one route at a time.

## 3. Optimistic concurrency

Every resource with a mutable representation carries `rowVersion` in its body and the same value as a
strong `ETag`.

| Operation | `If-Match` | Missing |
|---|---|---|
| `PUT` (overwrites fields the caller read) | **required** | `428 Precondition Required`, code `concurrency.version_required` |
| `POST` action (suspend, reactivate, …) | honored | allowed — the domain state machine already refuses the unsafe orderings |
| Stale version, any operation | — | `409 Conflict`, code `concurrency.conflict`, `retryable: false` |
| Malformed or weak (`W/`) validator | — | `400 Bad Request`, code `concurrency.malformed_version` |

**No last-write-wins.** A `PUT` without a precondition is refused rather than treated as "overwrite
whatever is there".

**Retry guidance is in the response**, because "409 Conflict" alone leaves a client guessing: the
problem document says re-read, re-apply, retry, and sets `retryable: false` so a client does not loop
on a version that can never succeed.

**Aggregates with children:** a write that changes only child rows must call
`MarkAggregateChanged` on the root. Otherwise the root's row is never updated, its version never
moves, and concurrent edits to the child collection both succeed — the failure this section exists to
prevent. Enforced by `ConcurrencyModelTests` for the model, and by `ConcurrencyTests` for behavior.

## 4. Errors

RFC 7807 `application/problem+json` for every failure, including the ones the framework produces
(404, 405, 415).

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.2",
  "title": "Authentication required",
  "status": 401,
  "detail": "The email or password is incorrect.",
  "code": "auth.invalid_credentials",
  "traceId": "00-d561c59…-ddefdd95…-01"
}
```

- **`code` is the contract.** Stable, dotted, lower snake_case: `<area>.<reason>`. Clients branch on
  it. `detail` is prose for humans and may be reworded at any time.
- **`title` and `status`** are derived from the error's `ErrorKind` in exactly one place
  (`ResultExtensions`), so the same failure never answers with two different codes.
- **Errors are deliberately coarse where precision would leak.** Sign-in answers
  `auth.invalid_credentials` for an unknown email, a wrong password, and an un-activated invitation
  alike; distinguishing them would make the endpoint an account-existence oracle. Where a code is
  vague, the audit trail records the specific reason.
- **`traceId`** is present on every problem and matches the request's `X-Correlation-Id`.

| `ErrorKind` | Status |
|---|---|
| `Validation` | 400 |
| `Unauthorized` | 401 |
| `Forbidden` | 403 |
| `NotFound` | 404 |
| `Conflict` | 409 |
| `Failure` | 400 |

Exceptions are mapped centrally and only for the three that carry a meaningful HTTP answer:
`PermissionDeniedException` → 403, `ConcurrencyConflictException` → 409, `DomainException` → 500.
Everything else is an unhandled 500, which is correct: business failures are `Result` values, so an
exception reaching the edge is a defect.

## 5. Validation

Three layers, and only the middle two enforce (AD-10):

1. **Client (zod)** — UX only. Never trusted.
2. **Application** — FluentValidation per command/query in the pipeline; the contract.
3. **Domain** — invariants in aggregates and `IBusinessRule`; the last line.

A rejected input is `400` with a `code`. Field-level detail belongs in the problem document's
per-field errors, not concatenated into `detail`.

## 6. Lists: pagination, filtering, sorting, searching

```
GET /api/v1/users?page=1&pageSize=25&sortBy=email&sortDir=asc&search=ada&status=Active
```

Envelope, identical for every list endpoint:

```json
{ "items": [], "page": 1, "pageSize": 25, "totalCount": 0, "totalPages": 0 }
```

- **`pageSize` is capped server-side** (200). Out-of-range paging is **clamped, not rejected** —
  paging is navigation, and a 400 on `?page=0` helps nobody. An unbounded `pageSize` must never reach
  the database.
- **`sortBy` maps through an explicit allowlist.** Never reflection, never string interpolation into
  a query: a sort parameter is caller-controlled input, and an unvalidated one is an injection point
  or an accidental full-table scan. An unknown value falls back to the default sort.
- **Sorting is always total.** Every sort ends with a tiebreaker on `id`; without one, two pages of an
  ambiguous sort can repeat or skip rows and the list appears to shuffle as the client pages.
- **`search`** is a case-insensitive substring match over a documented set of fields per endpoint.
- **Filters are named parameters**, not a query language. An unparseable filter value yields an empty
  page rather than a 400 — a stale filter in a bookmarked URL should show nothing, not an error.
- **Some lists are deliberately not sortable.** `/audit-logs` is always newest-first, so the
  descending `created_at` index always applies.

## 7. Naming

| Thing | Convention | Example |
|---|---|---|
| Path segment | plural, lower kebab-case | `/api/v1/audit-logs` |
| Sub-resource | nested under its parent | `/users/{userId}/roles` |
| Action | verb as a path segment, `POST` | `/users/{userId}/suspend` |
| Query parameter | camelCase | `pageSize`, `sortDir` |
| JSON field | camelCase | `rowVersion`, `lastLoginAtUtc` |
| Timestamp field | `…AtUtc`, ISO-8601 with offset | `createdAtUtc` |
| Enum value | the .NET name, `PascalCase` | `PendingActivation` |
| Error / rule code | `<area>.<snake_case_reason>` | `users.email_taken` |
| Permission | `<area>.<action>` | `users.manage` |
| Endpoint name (OpenAPI `operationId`) | PascalCase verb-first | `SetUserRoles` |

Actions are used where a state transition has its own rules and audit meaning — `suspend` is not a
`PATCH` of a status field, because the transition is guarded and the field is not directly settable.

## 8. Authorization

- **Deny by default.** Every endpoint declares a permission via `RequireAuthorization("area.action")`.
  An endpoint with no policy fails `EndpointConventionTests` (R11).
- **Anonymous and self-service endpoints are listed explicitly**, with a reason, in that test's
  exemption lists. A stale exemption also fails.
- **Every policy name must be a declared permission** — a typo produces a requirement nobody can
  satisfy, which is a 403 for every caller on an endpoint that looks correct. Gated.
- **Two enforcement layers.** The endpoint policy, and `[RequiresPermission]` on the request type
  re-checked by the pipeline. A route that lost its policy still cannot reach a handler.

## 9. OpenAPI

Every operation carries a `summary`, a `description`, a typed success response, and a
`ProducesProblem` for each failure a caller can provoke. Gated by R12 — an undocumented operation
fails the build, which is how `GET /roles/{roleId}` was caught shipping without a description.

`contracts/openapi-v1.json` is committed. Any change to the document fails
`OpenApiContractTests` until the snapshot is regenerated:

```
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests
git diff contracts/openapi-v1.json
```

It fails on additive changes too, by design: a tool that decided for itself which changes were
breaking would be wrong at the edges, so the diff goes in front of a reviewer instead.

**Examples** are carried by the descriptions rather than by `example` payloads. A hand-written example
drifts from the schema silently, and the schema plus a description of the semantics is what a client
generator actually consumes. Revisit if the console team asks for them.

## 10. Idempotency

`Idempotency-Key` (AD-39) lets a caller retry a write safely. A command that opts in
(`IIdempotentCommand`) has the pipeline claim the key before the handler runs and replay the stored
outcome on any retry, so the handler executes at most once per key.

**Request header**

| Header | Meaning |
|---|---|
| `Idempotency-Key` | Caller-chosen retry token. Required when the command declares `KeyRequired`; a request missing it is rejected before the handler runs. |

**Response headers**

| Header | Meaning |
|---|---|
| `Idempotency-Replayed: true` | Stamped when this response is a stored replay, not a fresh execution. Absent on a first execution. |
| `Retry-After` | Seconds until a concurrent duplicate is worth retrying. Present only alongside `idempotency.operation_in_progress`. |

**Outcomes**

| Code | Status | Meaning |
|---|---|---|
| `idempotency.key_required` | 400 | The command requires a key and none was sent. |
| `idempotency.operation_in_progress` | 409 + `Retry-After` | A concurrent duplicate already holds the claim; this request never ran. |
| `idempotency.key_reuse_mismatch` | 422 | Same key, a different request body — always a client bug, never a race. |
| *(replay)* | the original response's status | The stored outcome replayed verbatim, plus `Idempotency-Replayed: true`. |

An endpoint added before its command opts in must not invent its own retry scheme — the semantics
above are the only ones this API has.

## 11. Headers

| Header | Direction | Meaning |
|---|---|---|
| `X-Correlation-Id` | both | Request identity; echoed, minted when absent, and stamped on audit and outbox rows. |
| `ETag` | response | Current `rowVersion` of the resource, strong. |
| `If-Match` | request | The version the caller is updating. |
| `Location` | response | Where a created resource lives. |
| `Retry-After` | response | On 429, and on the 409 `idempotency.operation_in_progress` (§10). Absent on every other 409 — a stale-version conflict is not a matter of waiting. |
| `Idempotency-Key` | request | See §10. |
| `Idempotency-Replayed` | response | See §10. |
