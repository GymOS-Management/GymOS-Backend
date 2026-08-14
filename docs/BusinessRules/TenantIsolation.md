# Tenant Isolation — Business Rules

**Module:** cross-cutting (`BuildingBlocks.Application`/`Infrastructure`, `Platform.Infrastructure`,
`WebApi`) — it owns no schema of its own; it protects whichever schema a tenant-scoped table lives in.
**Status:** **mechanism built and proven — R14 (AD-49), enforced by a startup validation that did not
exist before this increment.** **No production table is tenant-scoped yet.** The first is `members`,
in the increment after this one; everything below describes machinery proven against a table that
exists only for the tests.
Design: [`docs/superpowers/specs/2026-08-14-r14-tenant-isolation-design.md`](../superpowers/specs/2026-08-14-r14-tenant-isolation-design.md).

## Purpose

GymOS runs every gym in one PostgreSQL database, not one database per gym. That is cheaper to operate
and cheaper to migrate, and it means the only thing standing between one gym's data and another's is
whatever code remembers to ask for the right rows. R14 exists because "whatever code remembers" is not
a control — it is a hope, and a hope with a body count: a handler copy-pasted from a platform-scoped
one, a query missing a `WHERE`, a job that loops gyms and forgets one filter on one path. This document
is the control that replaces the hope, and the reasoning a future maintainer needs before changing it.

## The rule, in one line

`tenant_id = the session's tenant, or no rows.`

A tenant-scoped table's every read and write is filtered by PostgreSQL itself, through row-level
security, not by application code remembering a `WHERE tenant_id = …` clause. The comparison lives in
the database, so a handler that forgets to filter does not leak rows sideways — it simply reads none
(see "Fail closed", below). That property, not the filtering itself, is the point of building this in
the database rather than in every handler.

## Where the tenant comes from

The tenant is resolved once per request, from the route. `/api/v1/gyms/{gymId}/…` names the tenant in
the URL; `TenantResolutionMiddleware` reads `gymId`, checks the caller's grants (`UserRole.TenantId`:
`null` means platform-wide, a value means a grant for that one gym), and — only if the caller may act
for that gym — sets it on the scoped `ITenantContext` for the rest of the request. A route with no
`{gymId}` is platform scope and sets no tenant at all.

`ITenantContext` is deliberately not part of `ICurrentUser`. Who the caller is and which tenant they
are acting for are different facts — a platform administrator operating on one gym has both at once —
and collapsing them into one object creates a natural home for the line that ends this control:
`if (user.IsPlatformAdmin) skipFilter()`. Keeping the two apart means there is no such line to write,
and reading the tenant is a separate capability (`ITenantContext`) from setting it
(`ITenantContextSetter`), so a handler cannot re-point its own scope even by accident.

A job that needs tenant data receives no tenant from any request; it loops gyms and sets the context
once per iteration, one gym at a time, through the same seam a request uses. Concretely, and as of
today, that means **a fresh DI scope per gym** — `_scopeFactory.CreateAsyncScope()` inside the loop,
then `ITenantContextSetter.SetTenant(gymId)` on that scope — which is the shape `FeatureOverrideSweepJob`
already has for unrelated reasons (one scope and one transaction per gym, so one gym's failure does not
abandon the sweep). `ITenantContext` is scoped and refuses to be re-pointed, so the fresh scope is what
makes the next iteration's tenant settable at all.

> **`TenantScope` does not exist.** The design lists a `using`-shaped helper (`TenantScope`) that wraps
> the scope-per-gym dance, and earlier revisions of this document referred to it as though it were
> built. It is **owed, not present** — nothing in `src/` or `tests/` defines it. It was not built with
> the R14 mechanism because there is no consumer yet: no production table is tenant-scoped, so no job
> loops tenants for tenant data, and an API guessed ahead of its first caller would be guessed wrong.
> Until it lands, write the scope-per-gym loop out longhand as above. Tracked in `docs/STATUS.md`.

The setting then has to survive the trip to PostgreSQL. `TenantSessionInterceptor` writes
`app.tenant_id` on **every** connection open — never merely reset — including an empty value for
platform scope, so nothing is ever read that this connection's current user did not just write.

**Why unconditionally, stated accurately.** Npgsql pools physical connections, and a plain session
`SET` outlives the logical connection that wrote it — but Npgsql also resets a pooled connection when
it is returned (`DISCARD ALL`/`RESET ALL`), which clears the setting before the next borrower sees it.
That reset is disabled by `No Reset On Close=true`, and is skipped entirely when multiplexing is
enabled; neither is set anywhere in this solution today. So the unconditional write is **defence in
depth over Npgsql's connection reset, not the sole thing preventing leakage**. This is measured rather
than argued: the interceptor was temporarily changed to skip the write when there is no tenant — the
shape this design rejects — and all eight isolation tests still passed, the pool's reset having covered
the gap. The unconditional write is kept because it is what stays correct the day someone turns that
reset off, or enables multiplexing, for reasons that have nothing to do with tenancy — and because a
control that depends on a connection-string default nobody is watching is not a control.

## Why a caller without a grant gets 404, not 403

A caller who holds no grant for `{gymId}` gets exactly the same answer as a caller who asked for a gym
that does not exist: **404**. "You may not see this gym" and "this gym does not exist" have to be
indistinguishable, or the status code becomes an oracle for enumerating tenants — a 403 confirms the
gym is real and merely off-limits; a 404 confirms nothing at all.

**This will read as a bug to whoever first hits it legitimately.** An operator who mistypes a gym id,
or whose grant was revoked five minutes ago, sees the same "not found" a genuinely nonexistent gym
produces, with nothing in the response to tell the two apart. That confusion is the cost of the
guarantee, not a defect in it. Recorded here so it is recognised as deliberate the day someone opens a
ticket saying "this gym definitely exists, why does the API say it doesn't" — the answer is: correctly.

A malformed `{gymId}` — a route value that is not a `Guid` — is also 404, and sets no tenant. It never
falls through to platform scope, which would be a silent widening of what the caller can see.

## Why there is no bypass role, and none will be added

No credential, flag, or role skips the tenant filter. Not a `SuperAdmin` acting as one, not a job that
wants "every tenant, in one query" — a job that needs cross-tenant data loops gyms and sets the context
once per gym, the same seam a request uses, one gym at a time.

This is deliberately the slower shape. A platform-wide sweep in a single query would outrun N queries,
one per gym, every time — and that cost is paid on purpose. A credential that can read every tenant's
data at once is exactly the kind of thing future code acquires by accident: one mis-resolved connection,
one convenience flag added under deadline pressure, and a job silently reads across every tenant with
nobody having decided it should. There is no bypass to reach for, so there is nothing for that mistake
to acquire. The extra queries are the guarantee, not a shortcoming of it — do not add the flag back in
the name of efficiency without re-reading this paragraph first.

## Why `FORCE` is mandatory, and why it is not enough on its own

PostgreSQL exempts a table's **owner** from that table's own row-level security policies, unless the
table also carries `FORCE ROW LEVEL SECURITY`. The application connects as `gymos`, and every migration
runs as `gymos`, so `gymos` owns every table it creates. Without `FORCE`, every policy in this design
would exist, read correctly in a code review, and filter nothing — which is why the startup validator
asserts `relforcerowsecurity` specifically, not merely `relrowsecurity`, and a tenant-scoped table
missing it fails the boot rather than passing a check that looked complete.

**`FORCE` stops the table's owner from bypassing its own policies. It does not stop a superuser, and
PostgreSQL gives no way to make it stop one: row security is never applied to a superuser, or to any
role carrying `BYPASSRLS`, whether or not the table is forced.** This is not a hypothetical for this
platform. Today's deployment connects to PostgreSQL as `gymos`, and the compose files that create that
role — `docker-compose.yml` and both its overlays, plus `appsettings.Development.json` — grant it
superuser privilege at `initdb` time. Against a superuser, `FORCE` buys nothing at all, and the
reasoning this design used to reject a separate, non-owner runtime role ("little is bought once `FORCE`
is asserted at boot") simply does not hold once the connecting role is a superuser rather than a plain
owner.

The validator closes this gap the only way it can from inside the application: it also refuses to boot
against a connected role that reports `rolsuper` or `rolbypassrls`. **That check has never actually
fired, because it only runs once at least one table implements `ITenantScoped`, and none does yet** —
so today's stack boots exactly as it always has, and the day `members` lands as the first tenant-scoped
table, the host will not start until whoever deploys it either changes which role the API connects as,
or knowingly accepts that this control is not real for that table. Changing the connecting role is
**not done here** — it touches three compose files, `appsettings`, CI, and needs either
`REASSIGN OWNED` or a volume destroy against every developer's existing database, because the initdb
scripts that create the role do not re-run against a volume that already exists. It is recorded as an
open decision for the maintainer, not as work this increment finished.

**The owner-bypass guard was proved, not merely asserted.** With `FORCE ROW LEVEL SECURITY` removed from
the test table, the isolation test that expects one tenant's row back reported
`Assert.Single() Failure: The collection contained 2 items` — the exact silent failure this whole
section exists to prevent — and passed again once the line was restored. A control that cannot be shown
failing is indistinguishable from one that does nothing; this one was shown.

## Fail closed: unset means nothing, not everything

The policy compares `tenant_id` to `current_setting('app.tenant_id', true)`, which reads as an empty
string under platform scope and casts to `NULL` for the comparison — and `tenant_id = NULL` matches no
rows, never every row.

This is the single most important property in the whole mechanism. A code path that forgets to set the
tenant — a handler wired to the wrong route, a job that reuses one scope across every gym instead of
taking a fresh one per gym (the seam `TenantScope` is owed for), a future maintainer who
"simplifies" the middleware ordering — does not therefore read every tenant's data. It reads **none of
it**. That failure is loud and immediate: an empty screen, a support ticket within minutes, a test that
fails the moment anyone looks. The alternative failure mode — reading everyone's data because nobody set
a filter — is silent, and the kind of thing that only surfaces in an incident review, if it surfaces at
all. The mechanism is built so that the mistake someone will eventually make produces the failure that
gets noticed, rather than the one that doesn't.

## The middleware does not check the gym exists — deliberately

`TenantResolutionMiddleware` reads `{gymId}` from the route, checks the caller's grants, and sets the
tenant. It never asks whether a gym with that id exists. A platform-wide caller — every caller today —
therefore gets a tenant set for **any** Guid they care to type.

This is intended, and it has a visible consequence once tenant-scoped tables land: `GET
/api/v1/gyms/{nonexistent}/members` will answer **`200 []`**, not `404`. An empty list for a gym that
was never real is precisely the "empty screen that reads as a bug" §8 of the design warns about, and it
will be reported as one.

It stays this way because the alternative is worse. An existence check here would have to answer
differently for "gym exists" and "gym does not exist" *before* the endpoint runs — which is exactly the
404-vs-empty oracle the 404-not-403 choice above exists to eliminate. Whether a given id is a real gym
would become readable from any gym-routed URL by any authenticated caller. Deciding that a request for a
nonexistent resource is a 404 is the **endpoint's** job, where it can be answered with the caller's
grants and the resource's own semantics in view; the middleware's job is only "may this caller act for
this tenant, and if so, which tenant". Do not add an existence check here to fix the empty-list report.

## Known open items

None of the following is fixed by this increment. All are recorded here, and in
`docs/STATUS.md`, because "the mechanism is built" is not the same claim as "the platform is isolated" —
that second claim waits on them.

- **The deployment connects as a PostgreSQL superuser**, which voids `FORCE` entirely against it — see
  "Why `FORCE` is mandatory", above. Nothing is exposed today because no production table is
  tenant-scoped yet; the validator's role check will refuse to boot the day one is, which is the correct
  failure, but changing which role the platform connects as is a decision for the maintainer, not
  something this increment did.
- **No gym-scoped grant can be issued through the API yet.** `SetUserRolesCommandHandler` rejects any
  attempt at a non-platform-scoped role and only ever writes `UserRole.TenantId = null`, so every grant
  any role holds today is platform-wide. That is the real reason `TenantResolutionMiddleware`'s grant
  check has never had a gym-scoped grant to check against — the existing test suite is entirely
  platform-scope callers. The Members increment needs a way to create a gym-scoped grant; it does not
  exist yet.
- **`TenantScope` is owed.** The design names a `using`-shaped helper that sets the tenant for one gym
  and clears it on dispose, so a job looping gyms cannot forget the second half. It was not built —
  there is no consumer for it yet, and an API guessed ahead of its first caller is guessed wrong — and
  until it exists, jobs must take a fresh DI scope per gym by hand (see "Where the tenant comes from",
  above). The Members increment, or the first job that reads tenant data, is where it earns its shape.
