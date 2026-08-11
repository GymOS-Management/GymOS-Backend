# M4.1 — Feature-flag review fixes — Design

**Status: approved 2026-08-12.** One product decision taken with the owner: redundant overrides are
cleared **synchronously for gym-scoped triggers and by a nightly sweep for platform-wide ones**.

**Origin:** the M4 whole-increment review (2026-08-12) — the gate M4 never received before merging.
One Critical, six Important, eight Minor. This increment fixes the Critical, all six Importants, the
contract additions a console will need, and the documentation that claims things the code does not do.

## 1. The Critical: an invariant enforced at exactly one instant

M4's headline claim, stated unconditionally in `Features.md`, `STATUS.md` and the M4 design:

> An override is stored **only while it differs** from what the gym would get anyway.

It is true at the moment of a `PUT /gyms/{id}/features` write, and nothing maintains it afterwards.
`GymFeatureOverride` is written from one handler and read by queries; nothing subscribes to
`PlanFeatureSet`, `PlanFeatureCleared`, `FeatureDefaultChanged`, or any subscription lifecycle event.

**The scenario that costs money.** A gym on `BASIC` (`crm=false`) is granted `crm=true`, reason
"3-month goodwill trial". Six weeks later it upgrades to `PRO`, which includes CRM. The override is
now redundant but remains. Two consequences follow, and the second is the expensive one:

1. `GET /gyms/{id}/features` reports `source: Override` with that trial reason for a capability the
   gym now pays for — the wrong answer in precisely the support conversation `source` exists to serve.
2. When the gym downgrades to `BASIC` a year later, the stale row wins and it keeps CRM it no longer
   buys. That is the *"frozen by a row nobody remembers creating"* outcome the mechanism exists to
   prevent, produced by the mechanism itself.

Three other triggers do the same: a subscription ending (the plan layer goes silent), a default
flipping via `PUT /features/{id}`, and a plan clearing an opinion.

**Why every earlier review missed it.** Every override test moves the override while the layers
beneath stand still. Not one changes a plan or a default *underneath* an existing override.

## 2. Re-evaluation: one rule, two schedules

**The rule is not new and must not be re-expressed.** An override is redundant exactly when
`FeatureResolution.RequiresOverride(stored, planEnabled, defaultEnabled)` returns `false` — the same
function the write path already calls. This increment adds a way to *apply* that rule to a gym's
existing exceptions; it does not add a second definition of what "redundant" means.

```
GymFeatureExceptions.Redundant(overrides, planOpinions, defaults) → the override ids to withdraw
```

Pure, total, no database — the `FeatureResolution` shape, for the same reason.

**Two schedules, because the triggers differ by orders of magnitude:**

| Trigger | Blast radius | When it is applied |
|---|---|---|
| Gym assigned a plan, changes plan, or its subscription ends | **One gym** | **Synchronously**, in the same transaction |

"Synchronously" means *in the transaction that changed the subscription*, wherever that happens —
which includes `RenewalJob`'s expire-and-cancel steps, not only operator-driven API calls. A
subscription that ends overnight must clear its gym's now-redundant exceptions in the same step, or
the guarantee below is false for exactly the case nobody is watching.
| A plan's feature opinion changes (`PUT /plans/{id}/features`) | Every gym on that plan | Nightly sweep |
| A platform default flips (`PUT /features/{id}`) | Every gym with an override on that feature | Nightly sweep |

Doing the platform-wide triggers synchronously was considered and rejected: editing one plan feature
would rewrite overrides for every gym on the plan inside the request, turning a quick billing edit
into an unbounded write, and making a billing action mutate feature rows couples two modules the
design deliberately kept apart.

**The guarantee therefore changes, and the documentation must say so** rather than continuing to
claim an invariant that holds instantaneously:

> A redundant exception is withdrawn **immediately** when the change is to that gym, and **within a
> day** when the change is to a plan or a platform default.

**`FeatureOverrideSweepJob`** runs daily at **03:50 UTC** — after `RetentionJob` (03:10) and
`PlatformStatsJob` (03:30) — on M3.4's existing runner, under its `IJobLock`. It re-evaluates gyms
that hold at least one override, which is a small population by construction. Withdrawal raises the
existing `GymFeatureOverrideCleared` event, so the audit trail records the withdrawal exactly as an
operator-driven one.

**It is built as "re-evaluate this gym's exceptions", not "delete redundant rows",** because override
**expiry** — still an open product decision, still deferred — is one more predicate in the same pass.
Building it any other way means building the hook twice.

## 3. Contract-shaping changes

These change the API a console will be written against, so they land before F4 rather than after.

### 3.1 `GET /gyms/{id}` stops leaking the feature block

Today the gym detail returns the full resolution — including the free-text `overrideReason` — behind
`gyms.read` alone. The sibling route `GET /gyms/{id}/features` requires `features.read` at *both* the
endpoint policy and the request type, so this bypasses both layers for identical data. No stock role
is affected (all four holding `gyms.read` also hold `features.read`), but `roles.manage` can create a
`gyms.read`-without-`features.read` role today, and `Features.md` says "the split is the point".

**The `features` block is omitted when the caller lacks `features.read`** rather than the route
requiring both permissions — a `gyms.read`-only role keeps a usable gym page, and least privilege is
served. The field becomes optional in the contract and the OpenAPI description says when it is absent.

All three contract additions (§3.1–3.3) move the committed OpenAPI snapshot. That is a deliberate
regeneration read semantically, not a byte-diff waved through: expect one field made optional and
three added, and nothing removed.

### 3.2 `ResolvedFeatureDetail` gains the override's provenance

`overrideSetAtUtc` and `overrideSetByEmail`, populated only when an override decided the answer.

The aggregate has recorded `SetAtUtc` and `SetByUserId` since M4; nothing exposes them. While
overrides never expire, *"how old is this grant, and who made it"* is the only compensating control an
operator has, and today it requires going to the audit log. Two additive fields now; a versioned
change once a console consumes the contract.

### 3.3 `FeatureListItem` gains `isDeclared`

A key removed or renamed in code leaves its row in place — correctly, since deleting would cascade
into plan contents and overrides. But `GET /features` then returns it indistinguishably from a live
feature: fully editable, contributing an adoption count. The only signal today is a startup warning.
`isDeclared` (false when no module declares the key) lets a console mark it, and is the difference
between an orphan being visible and being invisible.

### 3.4 `PUT /gyms/{id}/features` keeps its documented promise under a race

Two operators granting the same (gym, feature) concurrently both see "no existing override", both
insert, and one hits `ux_gym_feature_overrides_gym_feature`. The loser currently receives 409
`persistence.uniqueness_conflict` — *"Re-read the resource and choose a value that is still free"*,
`retryable: false`. Both halves are wrong: there is no other value to choose, and retrying the
identical request would succeed, because the second attempt finds the row and `Restate`s it.

**The handler retries once on that specific constraint**, converging on `Restate` — which makes the
documented promise ("the later decision wins, and the audit trail keeps the earlier one") true rather
than aspirational. A second failure is a real conflict and surfaces as one.

The no-`If-Match` design is **kept**: for different keys there is genuinely nothing to conflict over,
and the review agreed. It was the error body that was wrong, not the decision.

### 3.5 `PUT /plans/{id}/features` stops being a 500, and refuses contradictions

`{}` or `{"features": null}` binds `Features` to null and throws `NullReferenceException` — a caller
mistake reported as a server fault. The sibling gym route already writes `request.Features ?? []`; the
same guard applies here.

The same route also applies **last-one-wins** for a repeated key, where the gym route refuses it.
`FeatureErrors.DuplicateKey` exists and is documented; it is simply not wired in here. The reasoning
recorded for the gym route — *"applying the last would honour half a self-contradicting request while
reporting success"* — holds verbatim.

## 4. Correctness fixes with no contract impact

- **The plan-edit race.** `SetGymFeaturesCommandHandler` decides "does this need an override?" from a
  plan it read outside any guard, so a concurrent `PUT /plans/{id}/features` can make the answer stale
  between the decision and the save — returning 200 asserting an outcome the gym does not resolve to.
  The plan row is now read **`FOR UPDATE` inside the same transaction** (`FindByIdForUpdateAsync`'s
  existing precedent), so a concurrent plan edit serialises behind it.
- **The seeder boot race.** Two instances booting against a fresh database both insert twelve rows and
  one dies on `ux_features_key` — and because `FeatureCatalogSeeder` calls `SaveChangesAsync` directly
  rather than through `IUnitOfWork`, a raw `DbUpdateException` escapes and terminates the host.
  `IAdvisoryLock` (M1.3) wraps the seed. `PlatformIdentitySeeder` has the identical exposure and is
  fixed in the same pass, since it is one pattern, not two bugs.
- **`PUT /features/{id}` stops paying for adoption counts to return one row.** The read-back shares
  `Project()`, so every catalogue edit runs the full O(features × gyms) scan. It uses a count-free
  projection instead. The list endpoint keeps its counts — they are the reason that endpoint exists.
- **`ux_gym_subscriptions_one_live_per_gym` hardcodes `status IN (1,2,3)`** rather than deriving from
  `SubscriptionStatusTransitions.Live`. Nothing breaks today, but if `Live` ever grows, the index
  silently stops guaranteeing the invariant that `FeatureQueries`' `FirstOrDefault` and
  `SubscriptionRepository`'s `SingleOrDefault` disagree about. A test asserts the index filter matches
  the declared set, so the two cannot drift silently.

## 5. Documentation that claims things the code does not do

**`Features.md`, `STATUS.md` and the recorded M4 review verdict all state that resolution is "never
expressed in SQL".** `FeatureQueries.Project()`'s `AdoptionCount` is the full `override ?? plan ??
default` chain as a translated LINQ expression. The review traced the translation and **it is
correct** — but three documents assert a structural property the code does not have.

The claim is replaced by what is true: the rule has one **authoritative** definition in
`FeatureResolution`, and one deliberate SQL restatement for the adoption count, which exists because
counting adoption per gym in C# would mean loading every gym. **A test pins the two against each
other** over a matrix of gym states, because the deferred expiry work will add a fourth input to
`Resolve` and the SQL copy would otherwise drift — silently counting expired grants as live adoption,
which is the exact number the field exists to inform a retirement decision with.

The §1 guarantee is likewise reworded per §2, and `Feature.Rename` — named in the M4 design, built as
`Describe(name, description, category)` — gets its deviation recorded.

## 6. Out of scope, stated

- **Override expiry.** Still an open product decision (does an expiring override lapse silently, or
  does someone get told?), and the sweep this increment builds is where it will land. Unchanged.
- **`PlanQueries.GetAsync`'s four correlated subqueries per plan-feature row** — bounded at 12 × 4
  today, and rewriting it earns nothing until the catalogue grows.
- **`FeatureRepository.ListAllAsync` being tracked** — twelve rows into the change tracker for a key
  lookup. Harmless now; recorded.
- **`LogSynced` counting orphans inside "left untouched"** — true but conflated. Cosmetic.
- **The M4 console (F4).** This increment exists so that it can be built against a stable contract.

## 7. Verification plan

**Unit.** `GymFeatureExceptions.Redundant` over the matrix that matters: an override matching a plan
that now includes it, an override matching the default after a subscription ends, an override that
still differs (must survive), and a gym with no overrides. Each case asserted against
`FeatureResolution.RequiresOverride` rather than a re-derived expectation.

**Application.** The synchronous path: assigning a plan that already grants what an override granted
withdraws it in the same transaction; a plan that does not, leaves it.

**Integration, over real PostgreSQL.** The pinning test nobody wrote — **"a plan upgrade withdraws an
exception it has made redundant"** — plus its converse, that a downgrade does *not* resurrect a
withdrawn one. The sweep clears overrides made redundant by a plan-feature change and by a default
flip. `GET /gyms/{id}` omits the features block for a `gyms.read`-only role and includes it otherwise.
`{}` on the plan route is a 400, not a 500. Two concurrent identical grants both succeed. The SQL
adoption count agrees with `FeatureResolution` across the state matrix.

**Live.** Grant an override, upgrade the gym's plan to include the same feature, and read the gym's
resolution: the exception is gone and the source reads `Plan` — the Critical finding, demonstrated
fixed rather than asserted.
