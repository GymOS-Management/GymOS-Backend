# Gym-scoped grants and tenant-aware permission evaluation — design

**Status:** approved design, **not built**. Written 2026-08-15 to close the "no gym-scoped grant can be
issued through the API" open item with a decision rather than a one-line "owed". The Members increment
builds it, because Members brings the first `ITenantScoped` entity and is the only increment that can
prove it end to end.

**Supersedes nothing.** Extends R14 (`2026-08-14-r14-tenant-isolation-design.md`) and AD-22.

---

## 1. Why this is not "add an endpoint"

`docs/STATUS.md` and `docs/BusinessRules/TenantIsolation.md` both recorded this as a missing write path:
`SetUserRolesCommandHandler` rejects tenant-scoped roles and only ever writes `UserRole.TenantId = null`.
That is true and it is the smallest part of the problem. Three findings, all verified by reading the
code on 2026-08-15:

**F1 — permission evaluation has no tenant dimension, so a gym-scoped grant would confer its
permissions everywhere.** `IdentityQueries.GetEffectivePermissionsAsync` joins
`user_roles → roles → role_permissions → permissions` filtered on `UserId` alone; `UserSecuritySnapshot`
carries a flat `IReadOnlyCollection<string> Permissions` with no tenant. `PermissionEvaluator` answers
both enforcement layers from that snapshot. So the day a `UserRole` row carries a `TenantId`, its role's
permissions are held on **every** route, platform ones included. Issuing gym-scoped grants against
today's evaluator is a privilege-escalation defect, not an incomplete feature.

**F2 — the two enforcement layers cannot both see the acting tenant as the pipeline stands.**
`TenantResolutionMiddleware` runs *after* `UseAuthorization()` (`Program.cs`). The endpoint policy
(`PermissionAuthorizationHandler`) is therefore evaluated before any tenant is resolved, while
`AuthorizationBehavior` runs after it and would see one. M1.3's design rests on "both read the same
`IPermissionEvaluator`, so they cannot disagree" — a tenant-aware evaluator breaks that invariant
unless the ordering changes first.

**F3 — no tenant-scoped role can exist at all.** The only `Role.Create` call in `src/` is
`PlatformIdentitySeeder`, always `RoleScope.Platform`. `Role.Create` requires a `TenantId` when the
scope is `Tenant`, so gym roles are per-gym rows rather than shared definitions — and nothing creates
them. A grant endpoint would have nothing to grant.

**The order matters.** F1 is closed before F3 is opened, or there is a window in which the escalation
is reachable. F2 is closed before F1, or closing F1 breaks gym-routed authorization.

## 2. Decisions

**D1 — tenant resolution moves between `UseAuthentication()` and `UseAuthorization()`.**
Both enforcement layers then read one resolved tenant and M1.3's single-evaluator invariant survives
intact. The middleware's current placement is justified in its own remarks as "after
`UseAuthorization()`, because it reads the authenticated caller's grants" — that reasoning does not
hold: reading grants needs the authenticated *identity*, which `UseAuthentication()` supplies one line
earlier. Route values are populated before both, since `WebApplication` inserts `UseRouting` at the head
of the pipeline.

*Rejected:* resolving the tenant inside `PermissionAuthorizationHandler` from route values — it computes
"the acting tenant" in two places, which is the drift this repository keeps finding. *Also rejected:*
tiering the layers (policy asks "held anywhere", behavior asks "held here") — it abandons the
cannot-disagree property deliberately and makes the endpoint policy a coarse pre-filter.

**D2 — an unauthenticated caller on a gym-routed endpoint still gets 401, not 404.**
Under D1 the middleware would otherwise run before authorization and turn today's 401 into a 404. It
must instead return early — no grant check, no tenant — when there is no authenticated caller, and let
`UseAuthorization()` issue its 401. This preserves the documented contract and the OpenAPI snapshot, and
costs nothing against tenant enumeration: an anonymous caller never reaches the grant check either way.

**D3 — effective permissions are the union of platform-wide grants and grants naming the acting
tenant.** Formally, for acting tenant `t`: `permissions(user, t) = ⋃ { role.permissions | grant ∈
user.grants, grant.TenantId IS NULL OR grant.TenantId = t }`. Under platform scope (`t` is null) only
the `NULL` arm contributes — a gym-scoped grant confers nothing outside its gym. This is the fail-closed
direction and matches R14's D4.

**D4 — the snapshot is keyed by (user, acting tenant), not by user.** `CallerSnapshot` memoizes per
request, and one request acts for one tenant (`TenantContext` refuses a re-point), so the cache stays
correct with the tenant added to its key. The key must include the tenant anyway: a cache keyed by user
alone would serve a gym-scoped answer to a later platform-scope check within the same request if the
resolution order ever changed.

**D5 — a `UserRole` naming a tenant may only reference a `Role` belonging to that same tenant.**
`Role.TenantId` and `UserRole.TenantId` are two independent columns today and nothing makes them agree.
A grant pairing gym A's role with gym B's tenant id would be a silent cross-tenant escalation. Enforced
in `User.AssignRole` — which needs the role, not just its id — and again by a database constraint, since
the domain rule alone does not survive a direct write.

**D6 — a platform role may not be granted with a tenant id, and a tenant role may not be granted
without one.** The two scopes are not interchangeable and a grant that mixes them is a caller error, not
a default to be filled in.

**D7 — gym roles are provisioned with the gym, seeded from a code-canonical definition list.**
Same shape as `PlatformIdentitySeeder`'s declare-and-reconcile, per gym: `GymAdmin`, `Reception`,
`Trainer` (the roles AD-22 names), created when a gym is provisioned and reconciled on startup for gyms
that predate the definition. `IsSystem = true`, so they are undeletable and unrenamable and their grants
stay code-canonical. Custom per-gym roles remain future work and need no second mechanism — `Role`
already carries the scope.

**D8 — issuing a gym-scoped grant is gated on `users.manage` plus a grant for the gym.** A platform
administrator holding `users.manage` platform-wide satisfies both. A gym administrator can only reach
the endpoint through a `{gymId}` route their own grant covers, so the middleware's 404 already bounds
them to their own gym. No new permission key is introduced.

**D9 — the endpoint is `PUT /api/v1/gyms/{gymId}/users/{userId}/roles`, declarative like its platform
sibling.** The caller sends the roles the user should hold *at that gym*; the handler reconciles that
set and touches no platform grant and no other gym's grants. `If-Match` required, for the reason
`SetUserRolesCommand` documents. Keeping it under `/gyms/{gymId}` is what puts it behind
`TenantResolutionMiddleware` and therefore behind the grant check.

**D10 — `SetUserRolesCommand` keeps its platform-only contract.** It continues to reconcile only
assignments where `TenantId IS NULL` and continues to refuse tenant-scoped roles. Two endpoints with one
concern each, rather than one endpoint whose meaning depends on a nullable field.

## 3. Build order

Each step leaves the system correct; no step opens a hole the next one closes.

| # | Step | Closes |
|---|---|---|
| 1 | Move `TenantResolutionMiddleware` between authentication and authorization; early-return for anonymous callers | F2, D1, D2 |
| 2 | Thread the acting tenant through `IIdentityQueries` → `CallerSnapshot` → `PermissionEvaluator` | F1, D3, D4 |
| 3 | `User.AssignRole` takes the `Role`; add the cross-tenant check and its database constraint | D5, D6 |
| 4 | Per-gym role definitions + provisioning and startup reconciliation | F3, D7 |
| 5 | `SetGymUserRolesCommand` and its endpoint | D8, D9 |

Steps 1 and 2 are worth landing even if 3–5 slip: they close the escalation risk and leave the platform
exactly as capable as it is today.

## 4. Testing

The mechanism is a security control, so the tests that matter are the ones that fail when it is removed.
Each of these must be shown failing against the unfixed code before it is trusted — this repository has
five recorded cases of a guard whose test could not exhibit the failure it guarded.

| # | Test | What it catches |
|---|---|---|
| 1 | A user holding a gym-scoped role holds **none** of its permissions on a platform-scope route | F1, the escalation itself |
| 2 | The same user holds them on that gym's routes | D3's positive half — without it, test 1 passes with a permanently empty set |
| 3 | …and not on a *different* gym's routes | The tenant is compared, not merely present |
| 4 | Endpoint policy and pipeline behavior agree for a gym-scoped caller | D1; fails if the middleware is moved back |
| 5 | An anonymous gym-routed request is 401, not 404 | D2 |
| 6 | A grant pairing gym A's role with gym B's tenant id is refused, in the domain and by the database | D5 |
| 7 | Revoking a platform grant leaves gym grants intact, and vice versa | D10's separation |

Test 2 is the negative control for test 1 and is not optional: a tenant-aware evaluator that returns
nothing at all would pass test 1 and be useless.

## 5. Deliberately not decided here

- **What a gym administrator may do with members** — that is Members' own permission vocabulary, not
  this spec's.
- **Custom per-gym roles** (operator-defined, beyond D7's three). The mechanism supports them; no
  endpoint is designed for them.
- **Whether a gym-scoped grant survives its gym being suspended or deleted.** `Gym` has both states and
  the grant would outlive them. It needs a rule; it does not need one before Members, because no
  gym-scoped grant exists yet.
- **Whether `perm_hash` in the access token needs a tenant dimension.** The claim is issued but
  permissions are read from the database per request (M1.3), so this is a cache-invalidation question,
  not a correctness one.

## 6. Status of the claims in this document

Everything in §1 was read from the code on 2026-08-15 and is cited by file. §2's D1 rests on
`WebApplication` inserting `UseRouting` at the head of the pipeline when endpoints are mapped — that is
framework behaviour taken from documentation, **not observed here**, and step 1 must confirm route
values are populated at that point before the reorder is trusted. Nothing in this spec has been built or
executed.
