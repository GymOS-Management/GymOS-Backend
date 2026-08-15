# Tenant Isolation — Business Rules

**Module:** cross-cutting (`BuildingBlocks.Application`/`Infrastructure`, `Platform.Infrastructure`,
`WebApi`) — it owns no schema of its own; it protects whichever schema a tenant-scoped table lives in.
**Status:** **mechanism built and proven — R14 (AD-49), enforced by a startup validation that did not
exist before this increment — and now actually filters.** The deployment connects as a non-superuser
role that owns nothing (§3, below); the superuser-connection gap this document used to record here is
closed. **No production table is tenant-scoped yet.** The first is `members`, in the increment after
this one; everything below describes machinery proven against a table that exists only for the tests.
Design: [`docs/superpowers/specs/2026-08-14-r14-tenant-isolation-design.md`](../superpowers/specs/2026-08-14-r14-tenant-isolation-design.md)
(the mechanism) and
[`docs/superpowers/specs/2026-08-15-database-roles-design.md`](../superpowers/specs/2026-08-15-database-roles-design.md)
(the roles that make it actually run).

## The three database roles

The application never connects as a superuser. Three roles replace the single bootstrap account:

| Role | Attributes | Owns | Used by |
|---|---|---|---|
| `gymos_admin` | superuser — this is `POSTGRES_USER` | the cluster | humans, `psql`, maintenance |
| `gymos_migrator` | `NOSUPERUSER NOBYPASSRLS` | database `gymos`, its schemas, every table | the one-shot `migrator` compose service only |
| `gymos_app` | `NOSUPERUSER NOBYPASSRLS` | nothing | the API at runtime |

Created by `docker/postgres/initdb/10-roles.sql`, which reads both passwords via psql's `\getenv`
rather than shell-interpolating them into SQL — a password containing a single quote cannot break the
statement or inject anything.

### Why two non-superuser roles, not one

A single non-superuser role that owned its own tables would still not be enough. **An owner can
execute `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` and switch its own isolation off in one statement**
— no superuser privilege required, just ownership. Row-level security exists largely as a defence
against SQL injection, and an injected statement running as that owner disables the very control meant
to contain it, with nothing to notice until the next boot's validator run, potentially weeks later.

Splitting the role in two closes that gap by taking ownership away from the role that serves requests.
`gymos_migrator` owns every table and runs DDL; `gymos_app` owns nothing and holds no DDL rights at
all, so the identical injected statement is refused outright — a non-owner cannot switch off a policy
it does not own, full stop. This is the whole reason for the split: an owner *can* be talked into
disabling its own protection in a single statement; a non-owner cannot be, regardless of what SQL it is
tricked into running.

### The constraint this creates — stated plainly, because it is the one that will bite someone

`gymos_app`'s read/write access comes from `ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator`, declared
once at initdb time with no `IN SCHEMA` clause so it covers every schema and table created afterward,
including ones migrations haven't written yet. **Default privileges attach only to objects created *by
`gymos_migrator`*.** Anything that ever creates a table as another role — a manual `psql` session as
`gymos_admin`, a future tool that runs DDL under its own identity — produces a table `gymos_app` cannot
read, write, or even see with a plain `SELECT`, and the failure looks exactly like a permission bug
rather than anything pointing at privileges. Today only the migrator holds DDL rights, so the
constraint holds by construction — but that is precisely the kind of thing that stops holding quietly,
the day a second path to creating a table appears.

An Azure-managed deployment starting from an existing database needs the ownership-transfer procedure
and the explicit `GRANT … ON ALL TABLES` this constraint implies for objects that already exist,
recorded in [`docs/runbooks/managed-postgres-roles.md`](../runbooks/managed-postgres-roles.md).

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
once per iteration, one gym at a time, through the same seam a request uses. Concretely that means
**a fresh DI scope per gym** — `ITenantContext` is scoped and refuses to be re-pointed, so the fresh
scope is what makes the next iteration's tenant settable at all. `FeatureOverrideSweepJob` already has
that shape for unrelated reasons (one scope and one transaction per gym, so one gym's failure does not
abandon the sweep), though it sets no tenant: its tables are platform-scope.

**`TenantScope` (BuildingBlocks.Application) wraps that dance**, so the loop reads:

```csharp
await using TenantScope scope = TenantScope.For(gymId, _scopeFactory);
var members = scope.ServiceProvider.GetRequiredService<IMemberRepository>();
```

Resolve the iteration's services from `scope.ServiceProvider`. A service the job holds from its own
scope carries the job's tenant, not the iteration's, and nothing can detect that from inside —
it is the one part of this the type cannot enforce.

> **It owns a scope; it does not clear a context — a correction to the design's §4.** That section
> describes `TenantScope` as setting the tenant and *clearing it on dispose*. Clearing is impossible
> against the context this design specifies, and deliberately so: `SetTenant` refuses a re-point and
> offers no reset, because a second set would mean any query that already ran did so under the
> previous value. A clear would reopen precisely that hole. The second half of the pair is instead
> delivered by the scope holding the tenant ceasing to exist — the same guarantee, with no reset path
> to misuse. The design spec is left as the historical record; this document is the current one.

> **No production caller yet, and that is expected.** It is built ahead of Members, which brings the
> first `ITenantScoped` entity and so the first job with tenant data to loop. What is proved today is
> the scoping contract — six unit tests in `GymOS.BuildingBlocks.UnitTests`, including the two that
> fail if anyone reintroduces the set-and-clear shape. The end-to-end proof that RLS actually filters
> through a `TenantScope` needs a tenant-scoped table and belongs to that increment.

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
table also carries `FORCE ROW LEVEL SECURITY`. Every migration runs as `gymos_migrator`, so
`gymos_migrator` owns every table it creates. Without `FORCE`, every policy in this design would exist,
read correctly in a code review, and filter nothing for that connection — which is why the startup
validator asserts `relforcerowsecurity` specifically, not merely `relrowsecurity`, and a tenant-scoped
table missing it fails the boot rather than passing a check that looked complete.

**`FORCE` stops a table's owner from bypassing its own policies. It does not stop a superuser, and
PostgreSQL gives no way to make it stop one: row security is never applied to a superuser, or to any
role carrying `BYPASSRLS`, whether or not the table is forced.** This was not a hypothetical for this
platform: the deployment used to connect to PostgreSQL as `gymos`, the bootstrap superuser the
`postgres` image creates, and against a superuser `FORCE` bought nothing at all — the reasoning R14
originally used to reject a separate, non-owner runtime role ("little is bought once `FORCE` is
asserted at boot") did not hold once the connecting role was a superuser rather than a plain owner.

**This is now fixed.** The API connects as `gymos_app`, which is `NOSUPERUSER NOBYPASSRLS` and owns
nothing (§3, above), so `FORCE` governs it exactly as designed, and the owner-bypass risk is closed by
ownership rather than by `FORCE` alone — see "Why two non-superuser roles, not one", above. The
validator's role check — refusing to boot against a connected role reporting `rolsuper` or
`rolbypassrls` — is now unconditional rather than gated on some entity implementing `ITenantScoped`, so
it is exercised on every boot rather than waiting for `members` to land before it can even fire.
`gymos_migrator` still owns the tables and still needs `FORCE` to be bound by its own policies;
the validator continues to assert `relforcerowsecurity` unchanged.

**The owner-bypass guard was proved, not merely asserted.** With `FORCE ROW LEVEL SECURITY` removed from
the test table, the isolation test that expects one tenant's row back reported
`Assert.Single() Failure: The collection contained 2 items` — the exact silent failure this whole
section exists to prevent — and passed again once the line was restored. A control that cannot be shown
failing is indistinguishable from one that does nothing; this one was shown. The role split itself was
proved the same way: `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` is refused (SQLSTATE 42501) on the
`gymos_app` connection, and succeeds on the `gymos_migrator` connection running the identical statement
as the control — the pair that keeps the first assertion meaningful rather than merely green.

## Fail closed: unset means nothing, not everything

The policy compares `tenant_id` to `current_setting('app.tenant_id', true)`, which reads as an empty
string under platform scope and casts to `NULL` for the comparison — and `tenant_id = NULL` matches no
rows, never every row.

This is the single most important property in the whole mechanism. A code path that forgets to set the
tenant — a handler wired to the wrong route, a job that reuses one scope across every gym instead of
taking a fresh one per gym (the mistake `TenantScope` exists to make hard), a future maintainer who
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

None of the following still-open items is fixed by this increment; one item that used to be listed
here — the superuser connection — has since been closed and is kept below only as a struck-through
record. All open items are recorded here, and in `docs/STATUS.md`, because "the mechanism is built" is
not the same claim as "the platform is isolated" — that second claim waits on them.

~~The deployment connects as a PostgreSQL superuser, which voids `FORCE` entirely against it.~~ **Closed
by the database-roles increment (2026-08-15).** The API now connects as `gymos_app`
(`NOSUPERUSER NOBYPASSRLS`, owns nothing); see "The three database roles" and "Why `FORCE` is mandatory",
above.

- **No gym-scoped grant can be issued through the API yet — and issuing one today would be a
  privilege-escalation defect, not merely a gap.** `SetUserRolesCommandHandler` rejects any attempt at a
  non-platform-scoped role and only ever writes `UserRole.TenantId = null`, so every grant any role
  holds today is platform-wide. That is the real reason `TenantResolutionMiddleware`'s grant check has
  never had a gym-scoped grant to check against — the existing test suite is entirely platform-scope
  callers. **What a 2026-08-15 design pass found is that the write path is the small part:**
  `IdentityQueries.GetEffectivePermissionsAsync` filters on `UserId` alone and `UserSecuritySnapshot`
  has no tenant dimension, so a gym-scoped grant would confer its role's permissions on *every* route;
  and `TenantResolutionMiddleware` runs after `UseAuthorization()`, so the endpoint policy and the
  pipeline behavior could not both see the acting tenant. On top of that, nothing can create a
  tenant-scoped `Role` at all — the only `Role.Create` call in `src/` is the seeder, always
  `RoleScope.Platform`.
  **Designed, not built:** `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md` carries the
  decisions (tenant resolution moves between authentication and authorization; permissions become the
  union of platform grants and grants naming the acting tenant; per-gym roles provisioned with the gym)
  and a build order in which the escalation window never opens. The Members increment builds it.
- ~~**`TenantScope` is owed.**~~ **Built 2026-08-15** (`BuildingBlocks.Application/Tenancy`), six unit
  tests. It owns a DI scope per gym rather than clearing an ambient context — see "Where the tenant
  comes from", above, for why the design's "clear on dispose" could not be built as written. **It has
  no production caller yet**, which is the deliberate part: Members brings the first `ITenantScoped`
  entity and therefore the first job with tenant data to loop, and that increment is where the
  end-to-end proof (RLS filtering through a `TenantScope` against a real tenant-scoped table) belongs.
  What is proved now is the scoping contract, not the isolation it will carry.
