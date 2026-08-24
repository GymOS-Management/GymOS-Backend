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

## The three database principals

The application never connects as a privileged principal. Three replace the single bootstrap account:

| Principal | Holds | Owns | Used by |
|---|---|---|---|
| `sa` | `sysadmin` — the image's bootstrap login | the instance | humans, `sqlcmd`, maintenance |
| `gymos_migrator` | `db_ddladmin`, `db_datareader`, `db_datawriter`, `ALTER ANY SECURITY POLICY` | the `platform` and `outbox` schemas and everything in them | the one-shot `migrator` compose service only |
| `gymos_app` | `db_datareader`, `db_datawriter`, `EXECUTE` on `platform` | nothing | the API at runtime |

Created by `docker/sqlserver/initdb/10-principals.sql`, applied by the `db-init` service, which passes
both passwords as `sqlcmd` `:setvar` bindings rather than shell-interpolating them into SQL — a
password containing a quote cannot break the statement or inject anything.

### Why two unprivileged principals, not one

A single unprivileged principal that could change its own tables would still not be enough. Two
statements are enough to remove the protection, and neither needs `sysadmin`:

```sql
ALTER SECURITY POLICY platform.some_policy WITH (STATE = OFF);   -- needs ALTER ANY SECURITY POLICY
ALTER TABLE platform.some_table DROP COLUMN tenant_id;           -- needs ALTER on the table
```

Row-level security exists largely as a defence against SQL injection, and an injected statement
running as a principal holding either right disables the very control meant to contain it, with
nothing to notice until the next boot's validator run — potentially weeks later.

Splitting in two closes that gap by taking both rights away from the principal that serves requests.
`gymos_migrator` owns the schemas and runs DDL; `gymos_app` owns nothing, is in no role carrying DDL
or security administration, and so is refused outright. This is the whole reason for the split: a
privileged principal *can* be talked into disabling its own protection in a single statement; an
unprivileged one cannot be, regardless of what SQL it is tricked into running.

### What moving from PostgreSQL changed here

The split is unchanged. One of its three original justifications became unnecessary, and the statement
to worry about changed:

| PostgreSQL | SQL Server |
|---|---|
| A superuser or `BYPASSRLS` role is exempt from every policy **at read time** | **No read-time exemption exists.** A security policy filters every principal — schema owner, `dbo`, `sysadmin` — so a privileged connection sees no more rows than anyone else |
| A table's owner is exempt from its own policies unless `FORCE ROW LEVEL SECURITY` is set | **No owner exemption exists**, so there is no `FORCE` to set and no flag whose absence could quietly void a policy |
| The reachable attack is `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` | The reachable attack is `ALTER SECURITY POLICY … WITH (STATE = OFF)` — a bigger hammer, since it disables the policy for everyone rather than exempting one role |
| The validator checks `rolsuper`, `rolbypassrls`, `relrowsecurity`, `relforcerowsecurity`, `pg_policy`, `pg_has_role` | The validator checks role membership, `ALTER ANY SECURITY POLICY`, `CONTROL`, `sys.security_policies.is_enabled`, and per-table `ALTER` |

The net effect is that the *read* path is harder to subvert than it was and the *disable* path is
what remains — which is exactly what `gymos_app`'s permissions are arranged to prevent, and what
`TenantIsolationValidator` refuses to boot without.

### The constraint this used to create, and why it is gone

`gymos_app`'s read/write access is `db_datareader` and `db_datawriter` — two fixed database roles,
granted once, that cover **every table in the database**, including ones no migration has written yet.

That is a genuine improvement over the PostgreSQL arrangement rather than a translation of it. There,
the same access came from `ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator`, and default privileges
attach only to objects created *by the role they were declared for*. Anything that ever created a
table as another role — a manual `psql` session, a future tool running DDL under its own identity —
produced a table `gymos_app` could not read, write, or even see, and the failure looked exactly like a
permission bug rather than anything pointing at privileges. This document recorded that as the
constraint most likely to bite someone. Database-wide role membership has no such hole: it does not
care who created the table.

**Two things did not carry over, and both are exceptions to watch.**

`db_datareader` and `db_datawriter` cover tables and nothing else — not procedures. `gymos_app` needs
`EXECUTE` on two modules that issue DDL on its behalf (`platform.next_invoice_number`,
`platform.ensure_audit_log_partitions`), so `10-principals.sql` issues a standing
`GRANT EXECUTE ON SCHEMA::platform`. That grant is the real counterpart of `ALTER DEFAULT PRIVILEGES`,
it covers every module a future migration adds, and it is the one line whose absence produces the old
failure mode: a permission error on something a migration only just introduced.

And `GRANT VIEW DEFINITION ON SCHEMA::platform`, which has no PostgreSQL counterpart at all —
`pg_policy` was readable by anyone. **SQL Server's catalog views are permission-filtered**, and a
security policy is an object `gymos_app` holds no permission on, so without this grant
`SELECT COUNT(*) FROM sys.security_predicates` returns **0** on the application's connection against a
database whose predicates plainly exist.

That is not cosmetic, because `TenantIsolationValidator` deliberately runs on the application's own
connection: the question worth asking is "is the principal I am about to serve requests as actually
filtered", and a privileged connection answers a different one. Without the grant the validator reads
its own protection as absent and refuses to boot — failing closed, which is the right direction, but
for a reason that is false and with a message pointing at a missing migration. It cost a full suite
run to find. `VIEW DEFINITION` is metadata visibility only: it confers no right to read a row, call a
module, or change anything.

An Azure-managed deployment needs these principals created as contained users rather than logins, and
the ordered statements are in
[`docs/runbooks/managed-sqlserver-roles.md`](../runbooks/managed-sqlserver-roles.md).

## Purpose

GymOS runs every gym in one SQL Server database, not one database per gym. That is cheaper to operate
and cheaper to migrate, and it means the only thing standing between one gym's data and another's is
whatever code remembers to ask for the right rows. R14 exists because "whatever code remembers" is not
a control — it is a hope, and a hope with a body count: a handler copy-pasted from a platform-scoped
one, a query missing a `WHERE`, a job that loops gyms and forgets one filter on one path. This document
is the control that replaces the hope, and the reasoning a future maintainer needs before changing it.

## The rule, in one line

`tenant_id = the session's tenant, or no rows.`

A tenant-scoped table's every read and write is filtered by the database itself, through row-level
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

The setting then has to survive the trip to the database. `TenantSessionInterceptor` writes
`app.tenant_id` into `SESSION_CONTEXT` on **every** connection open — never merely reset — including a
NULL for platform scope, so nothing is ever read that this connection's current user did not just
write.

**Why unconditionally, stated accurately.** SqlClient pools physical connections, and session context
outlives the logical connection that wrote it — but SqlClient also issues `sp_reset_connection` when a
pooled connection is handed out again, which clears it before the next borrower sees it. So the
unconditional write is **defence in
depth over the driver's connection reset, not the sole thing preventing leakage**. This is measured rather
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

## There is no `FORCE`, and none is needed

**SQL Server has no owner exemption from row-level security, and no `BYPASSRLS`.** A security policy
filters every principal that queries the table — the schema owner, `dbo`, `sysadmin`, all of them.
There is no flag to set, and therefore no flag whose absence could leave a policy present, correct in
review, and filtering nothing.

This section used to be the longest and most anxious in the document, and it is worth recording why,
because the anxiety was justified under the old engine and is not under this one. PostgreSQL exempts a
table's owner unless `FORCE ROW LEVEL SECURITY` is set, and exempts a superuser or `BYPASSRLS` role
unconditionally — `FORCE` included, with no way to make it stop one. Every migration ran as
`gymos_migrator`, so that role owned every table; the deployment originally connected as the
bootstrap superuser, against which `FORCE` bought nothing at all. Two separate exemptions, one of them
unfixable, both silent.

Neither exists here. What remains reachable is **disabling** the policy —
`ALTER SECURITY POLICY … WITH (STATE = OFF)` — which is a larger effect (it switches the policy off for
everyone, not just for one principal) but a much narrower privilege: it needs
`ALTER ANY SECURITY POLICY`, which `gymos_app` does not hold and is in no role that carries. The
control therefore rests entirely on the principal split described above, rather than on a per-table
flag that a superuser could ignore.

**The validator was re-pointed accordingly, not relaxed.** It refuses to boot when the connected
principal is in `sysadmin`, `db_owner`, `db_ddladmin` or `db_securityadmin`, or holds
`ALTER ANY SECURITY POLICY` or `CONTROL`, or can `ALTER` a tenant-scoped table. It also asserts that
each tenant-scoped table has an **enabled** FILTER predicate and an **enabled** BLOCK predicate —
enabled specifically, because a disabled policy leaves every row in `sys.security_predicates` exactly
where it was while filtering nothing. That check is the direct heir of `relforcerowsecurity`: the one
that catches a control which looks installed and is not.

**Both halves were proved, not merely asserted.** `TheOwnerDoesNotBypassThePolicy` reads the probe
table as the schema owner with no tenant set and gets zero rows; running
`ALTER SECURITY POLICY … WITH (STATE = OFF)` immediately above that assertion turns it into
`Expected: 0, Actual: 2`, so it detects the condition rather than restating the manual. The principal
split was proved the same way, as a pair:
`ALTER SECURITY POLICY platform.tenant_probe_isolation WITH (STATE = OFF)` fails on the `gymos_app`
connection and succeeds on the `gymos_migrator` connection running the identical statement — the
control that keeps the first assertion meaningful rather than merely green. The pairing matters more
than it did under PostgreSQL, whose 42501 said "insufficient privilege" outright: SQL Server answers
33268, *"Cannot find the object … because it does not exist or you do not have permissions"*,
deliberately conflating absent with forbidden, so only the companion test proves which one happened.

## Fail closed: unset means nothing, not everything

The predicate compares `tenant_id` to `CONVERT(uniqueidentifier, SESSION_CONTEXT(N'app.tenant_id'))`,
which is NULL under platform scope — and `tenant_id = NULL` matches no rows, never every row.

Simpler than it was, and deliberately so. PostgreSQL session settings are text, so platform scope wrote
an empty string and the policy needed `NULLIF(…, '')::uuid` to turn it back into NULL. Session context
holds a typed `uniqueidentifier`, so NULL is written directly and no conversion is left that could
throw. An empty string here would in fact be worse: `CONVERT(uniqueidentifier, N'')` is a conversion
error rather than a NULL, so every platform-scope query would fail instead of returning nothing.

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
(owns nothing, holds no DDL or security administration); see "The three database principals" and
"There is no `FORCE`, and none is needed", above. The move to SQL Server closed it a second time, by
removing read-time exemptions from the engine altogether.

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
