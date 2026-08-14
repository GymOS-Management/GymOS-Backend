# R14 — Tenant isolation, and the machinery that proves it

**Status:** **approved 2026-08-14**, not yet built.
**Rule it implements:** AD-49 **R14** — "Every tenant-scoped table: `tenant_id` + RLS policy",
enforcement "startup persistence validation". That validation does not exist; this increment is it.
**Blocks:** the Members domain (`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`), whose `Member` is the
platform's first tenant-scoped entity.

## 1. Why this is its own increment

R14 has never run. Every module built so far — identity, gyms, plans, subscriptions, invoices, jobs,
features, analytics — is platform-scoped, so the rule has had no subject and no code. `M2_READINESS.md`
called this out twice and gave the instruction this increment follows: *"Write the RLS startup
validation with the first tenant-scoped entity, not after."*

Building it **before** Members rather than inside it is a deliberate choice. Mixing a novel security
control with a novel domain means that when the increment runs long — and a domain increment always
does — the temptation is to ship the domain and defer the control. Isolation retrofitted onto a
populated table is how cross-tenant leaks reach production.

**The whole increment is mechanism.** No production table becomes tenant-scoped here. The first one is
`members`, in the increment after this, and the validation built here catches it on its first boot
rather than being remembered later.

## 2. Scope

### In

- `ITenantContext` and its request-pipeline resolution
- `ITenantScoped`, the marker the validator keys on
- The connection-level session variable, written on every connection open
- Policy SQL in a migration, applied to the test-only table this increment creates
- `TenantIsolationValidator` — the startup persistence validation R14 names
- Six integration tests against real PostgreSQL (§ 6)

### Out, and why

| Not building | Why |
|---|---|
| Any production tenant-scoped table | This is the mechanism. `members` arrives with its own increment and its own design. |
| A tenant-scoped **role** or grant model | `UserRoleAssignment.TenantId` already exists and already carries gym-scoped grants. This increment reads it; it does not redesign it. |
| A bypass role for jobs | Deliberately never built — see D3. |
| Tenant resolution from a subdomain or header | Rejected in D2. Adding it later is additive; the route form is the one the API already has. |
| Cross-tenant reporting | Needs a decision about who may see across tenants and how it is audited. Nothing needs it today. |
| Retrofitting `OutboxMessage`/`IdempotencyRecord` | Both carry a nullable `TenantId` as a *stamp*, not as an isolation boundary — they are platform-owned infrastructure rows. Making them tenant-scoped is a separate question with its own consequences for the outbox processor. |

## 3. The decisions that shape the build

### D1 — The tenant is not part of identity

`ITenantContext` is its own scoped service exposing `Guid? TenantId` and `bool IsPlatformScope`. It is
**not** added to `ICurrentUser`.

Who the caller is and which tenant they are acting for are different facts, and a platform
administrator operating on one gym has both at once. Collapsing them into one object invites the line
that ends this control: `if (user.IsPlatformAdmin) skipFilter()`. Keeping them apart means there is no
natural place to write it.

### D2 — The tenant comes from the route, checked against the caller's grants

`/api/v1/gyms/{gymId}/members/…` names the tenant in the path. The pipeline resolves `gymId`, confirms
the caller holds a grant for that gym or is platform-scoped, and sets the context for the request. A
route with no `gymId` is platform-scope and sets no tenant.

Explicit and auditable, and it matches the URL shape M2's gym endpoints already use. The alternative —
inferring the tenant from the caller's single grant — becomes ambiguous the moment somebody holds
grants for two gyms, which franchise staff do by design (§14.4 of the Members design).

**A caller without a grant gets 404, not 403.** "You may not see this gym" and "this gym does not
exist" must be indistinguishable, or the error code becomes an oracle for enumerating tenants.

### D3 — Jobs loop tenants; no bypass exists

A job that needs tenant data iterates gyms and sets the context per iteration, through the same seam a
request uses — the shape `FeatureOverrideSweepJob` already has, one scope per gym.

**No bypass role and no "run as platform" flag is built at all.** Not because a platform-wide sweep
would not be faster, but because a credential that can read every tenant's data is the kind of thing
future code acquires by accident: one mis-resolved connection and a job inherits it silently. The cost
is that a platform-wide sweep is N queries instead of one. That cost is the guarantee.

### D4 — Unset means nothing, not everything

The policy compares `tenant_id` to `current_setting('app.tenant_id', true)`, which is null when unset,
so the comparison matches no rows.

A code path that forgets to set the context therefore reads **nothing**. It fails closed, visibly and
emptily, instead of failing open and silently. This is the single most important property in the
design: the mechanism's default state is safe, so a mistake surfaces as "no data" — which somebody
reports within minutes — rather than as "everyone's data", which nobody notices.

### D5 — `FORCE ROW LEVEL SECURITY`, because the app owns its tables

PostgreSQL exempts a table's owner from that table's policies. The application connects as `gymos`,
migrations run as `gymos`, so `gymos` owns every table. Without `FORCE`, every policy in this design
would exist, read correctly in review, and never filter a single row.

So every tenant-scoped table sets `FORCE ROW LEVEL SECURITY`, and the validator asserts
`relforcerowsecurity` — not merely `relrowsecurity`. **Checking only the latter is the version of this
increment that looks complete and does nothing.**

The alternative, a separate non-owner runtime role, is stronger in principle and rejected for now: it
splits the connection string in two, complicates migrations and the Docker compose story, and buys
little once `FORCE` is asserted at boot. Recorded here so a future increment can revisit it rather
than rediscover it.

### D6 — The setting is written on every connection open, never merely reset

Npgsql pools physical connections. A plain `SET app.tenant_id` outlives the logical connection, so the
next request to borrow that physical connection inherits the previous tenant's context — intermittent,
invisible, and it exposes *more* data rather than less.

A `DbConnectionInterceptor` therefore writes the value on **every** open, unconditionally, including
writing an empty value for a platform-scope request. Nothing is ever read that this request did not
write.

This is deliberately not "set it when there is a tenant, reset it when there is not": that shape has an
execution path where neither branch runs, and the bug it produces is the one above.

`SET LOCAL` would also be immune, being transaction-scoped — rejected because EF issues plain reads
outside a transaction, so it would either silently not apply or force a transaction around every query.

### D7 — A marker interface, so the architecture test can see it too

`ITenantScoped` (`Guid TenantId`) marks an entity as tenant-scoped. The validator walks the EF model
for implementers and checks their tables.

A marker rather than an EF annotation because NetArchTest can also assert it — a future tenant module's
entity that forgets the marker fails a test, rather than quietly opting out of isolation and passing
every gate.

### D8 — The validator fails the boot, and does not swallow

On a missing column, missing RLS, missing `FORCE`, or missing policy, it throws. `Program.cs` already
logs a startup exception as Fatal and rethrows, so the host dies and the container restarts.

That is the right trade for this control specifically: a platform that will not start is a visible
outage; a platform serving cross-tenant reads is an invisible breach.

**It must not copy the existing startup maintainers' error handling.**
`AuditLogPartitionMaintainer` and `IdempotencyRecordMaintainer` swallow `NpgsqlException` so a
schemaless database can boot. This validator skips only when migrations are genuinely pending — the
guard `SuperAdminSeeder` already has, for a real first-boot state — and treats everything else as
fatal. A security control that swallows "the table isn't there" passes on an empty database and on
every misconfiguration that resembles one.

## 4. Components

| Component | Where | Responsibility |
|---|---|---|
| `ITenantContext` | BuildingBlocks.Application | The acting tenant for this scope; set once, read many |
| `TenantContext` | BuildingBlocks.Infrastructure | Scoped implementation with a one-shot setter |
| `ITenantScoped` | BuildingBlocks.Application | Marker: this entity's rows belong to a tenant |
| `TenantResolutionMiddleware` | WebApi | Route `{gymId}` → grant check → context |
| `TenantSessionInterceptor` | Platform.Infrastructure | Writes `app.tenant_id` on every connection open |
| `TenantIsolationValidator` | Platform.Infrastructure | R14's startup persistence validation |
| `TenantScope` | BuildingBlocks.Application | `using` helper for jobs: set for one gym, clear on dispose |

## 5. Data flow

```
request  /api/v1/gyms/{gymId}/members
   │
   ├─ TenantResolutionMiddleware
   │     gymId from route → caller holds a grant? (else 404)
   │     → ITenantContext.TenantId = gymId
   │
   ├─ handler → DbContext → connection opened
   │     └─ TenantSessionInterceptor writes app.tenant_id
   │        (the gym's id, or empty for platform scope — always written)
   │
   └─ query → RLS policy: tenant_id = current_setting('app.tenant_id', true)::uuid
              unset → no rows

job
   foreach gym: using TenantScope(gym) → same interceptor → same policy
```

## 6. Testing

Six integration tests against real PostgreSQL, on a table created only in the integration-test schema.

| # | Test | What would otherwise hide |
|---|---|---|
| 1 | Tenant A's read returns none of tenant B's rows | The basic guarantee |
| 2 | A write lands under the acting tenant and cannot be forged to another | An insert that names someone else's tenant |
| 3 | **The owner does not bypass** | Without `FORCE`, this passes while filtering nothing (D5) |
| 4 | **No tenant set reads nothing, not everything** | Fail-closed asserted rather than assumed (D4) |
| 5 | **A tenant-scoped table with no policy refuses to boot** | The validator's own negative test |
| 6 | **The setting does not survive into the next use of a pooled connection** | The leak in D6 |

Two need specific construction to be real rather than decorative:

**Test 3** must be shown to fail without `FORCE` — drop it, watch the test fail, restore it. A negative
control at the time of writing, recorded in the commit. This session found four separate guards that
could not exhibit the failure they guarded; this is the control where that would matter most.

**Test 6** needs **`Maximum Pool Size=1`** on the test connection string, which forces every logical
open onto one physical connection and makes leakage deterministic. Without it the test passes on a pool
that happened not to reuse, and proves nothing.

The suite calls the **real** `TenantIsolationValidator`, not a reimplementation, so relaxing the boot
check fails test 5 with it.

## 7. Definition of done

- All six tests green against real PostgreSQL, with test 3's negative control recorded
- `pnpm`-side unaffected; backend `dotnet build` 0 warnings, `dotnet format` clean, full suite green
- An architecture test asserting every `ITenantScoped` entity's table is validated
- `docs/BusinessRules/` gains the tenant-isolation rules, and `ARCHITECTURE-ADDENDUM.md`'s R14 row can
  finally be marked as having a subject
- No production table is tenant-scoped by this increment

## 8. Risks

- **The mechanism is proven only against a test table.** Mitigated by the validator being generic and
  by Members arriving next; the risk is that a real table has a wrinkle the test table lacks — most
  plausibly a composite key or a join table, which `members` is not.
- **`Maximum Pool Size=1` changes test timing** and could mask or create ordering effects in the suite
  that uses it. Confined to the tenant-isolation tests' own host.
- **Middleware ordering.** Tenant resolution must run after authentication (it reads grants) and before
  anything that opens a connection. Getting this wrong fails closed — no tenant, no rows — which is
  the safe direction, but the symptom is "empty screen", not "error".
- **The 404-not-403 choice** will read as a bug to whoever first hits it legitimately. Documented in
  the business rules so it is recognised as deliberate.
