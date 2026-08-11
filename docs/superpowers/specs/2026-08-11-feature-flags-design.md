# M4 — Feature Flags — Design

**Status: approved 2026-08-11.** Three product decisions taken with the owner: an override that
agrees with the plan is **cleared**, not stored; a gym with no live subscription resolves to
**platform defaults**; `is_premium` is **informational** in M4.

**Frozen scope** (ARCHITECTURE.md §2.4, §3.4, §3.5, roadmap item 5): the `features` catalogue,
`plan_features`, `gym_feature_overrides`, and the resolution endpoint. Three-layer resolution:
**gym override → plan feature → platform default.**

## 1. What resolution means, precisely

For one gym and one feature, the answer is the first layer that has an opinion:

| Layer | Source | When it applies |
|---|---|---|
| 1. Gym override | `gym_feature_overrides` row | Whenever one exists — a deliberate, reasoned exception |
| 2. Plan feature | `plan_features` row for the gym's **live** subscription's plan | No override, and the plan has a row for this feature |
| 3. Platform default | `features.default_enabled` | Nothing above said anything |

Every resolved answer carries **where it came from** (`source: Override | Plan | Default`), because
"is POS on?" and "why is POS on?" are the same question in a support conversation, and an operator
who cannot see the source cannot tell a deliberate exception from a plan they misremember.

**A gym with no live subscription resolves at layer 3** (approved). A gym is provisioned before it
is sold anything — M2 supports exactly that order — so a provisioning gym is configurable and
demoable, and a cancelled gym degrades to the platform baseline rather than going dark.

## 2. The catalogue is code-canonical, its copy is not

`features.key` follows the AD-22 declare-and-validate pattern the permission catalogue already
uses: `PlatformFeatures.All` in the Domain declares the twelve keys §2.4 names (`pos`, `inventory`,
`crm`, `nutrition`, `workout_builder`, `member_app`, `ai_assistant`, `gateway`, `access_control`,
`marketing`, `analytics`, `reports`), a startup seeder inserts missing rows, and **no endpoint
creates or deletes a feature** (§3.5: "catalog keys are seed-managed, not runtime-creatable"). A key
is a compile-time constant that code branches on; inventing one at runtime would create a flag
nothing reads.

**But `name`, `description`, `category`, `default_enabled` and `is_premium` are editable at runtime**
(§3.5's `PUT /features/{id}`), so the seeder must **insert-only, never update** — otherwise every
restart would revert an operator's deliberate edit. This is the one place this catalogue differs from
permissions, whose rows are fully code-owned, and it is worth the difference: the platform default
for a feature is a commercial decision that must not require a deployment.

## 3. Domain

- **`Feature`** (aggregate root, platform-scope, audited): `Key` (immutable), `Name`,
  `Description`, `Category`, `DefaultEnabled`, `IsPremium`. Behavior: `Rename`,
  `Describe`, `SetDefault`, `SetPremium`. No delete — a feature key appears in code, and the
  reason plans are never deleted applies here too.
- **`GymFeatureOverride`** (aggregate root, platform-scope, audited): `GymId`, `FeatureId`,
  `IsEnabled`, `Reason`, `SetByUserId`, `SetAtUtc`. Unique on `(gym_id, feature_id)`.
  **`Reason` is required** — an override is an exception to what the gym pays for, and an exception
  without a stated reason is indistinguishable from a mistake six months later. (§2.4 lists the
  column; making it mandatory is this design's decision, and the audit's lesson about
  documented-but-unenforced intent argues for enforcing it in the aggregate.)
- **`PlanFeature`** — part of the `SubscriptionPlan` aggregate (`plan_id`, `feature_id`,
  `is_enabled`), reached through the plan exactly as `PlanPrice` is. This is the `plan_features`
  table M3.1 deliberately deferred *because the `features` catalogue did not exist*; it exists now.
- **`FeatureResolution`** — a pure domain calculator over (override?, planFeature?, feature):
  returns `(bool Enabled, FeatureSource Source)`. Pure and total, so the three-layer rule is
  testable without a database and cannot drift between the endpoints that read it.

`is_premium` gates nothing (approved): it is carried and displayed. Anything stronger is a pricing
rule nobody has specified, and blocking overrides on premium features would remove the operator's
ability to grant a goodwill trial.

## 4. Persistence

Migration `AddFeatures`: `platform.features` (unique `key`, `HasMaxLength` per the existing
conventions), `platform.plan_features` (unique `(plan_id, feature_id)`, FK plan → cascade like
`plan_prices`, FK feature → restrict), `platform.gym_feature_overrides` (unique
`(gym_id, feature_id)`, FK gym → cascade — an override is meaningless without its gym; FK feature →
restrict; FK set_by → users, `SetNull`, provenance only, the `PlanPriceId` precedent).

All three tables join `PlatformAuditedEntities`: who granted a gym a paid feature for free is
exactly the sort of thing a later billing conversation turns on.

`xmin` concurrency tokens on `Feature` and `GymFeatureOverride`. `PlanFeature` is exempt for
`PlanPrice`'s recorded reason (mutated only through the plan, whose token guards it, with
`MarkAggregateChanged`).

## 5. Application & endpoints (the frozen §3.4/§3.5 surface)

| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | `/features` | `features.read` | Catalogue + **adoption count** per feature (gyms resolving it on) |
| PUT | `/features/{id}` | `features.manage` | Name/description/category/default/premium. `If-Match` **required** (overwrites what the caller read — the M3.1 price lesson from the audit) |
| GET/PUT | `/plans/{id}/features` | `billing.read` / `billing.manage` | The plan's feature set. Billing permissions, not feature ones: this is what the platform *sells* |
| GET/PUT | `/gyms/{id}/features` | `features.read` / `features.manage` | Resolved flags with sources; PUT sets **or clears** overrides |

**Two new permissions**, `features.read` and `features.manage`, added to `PlatformPermissions` and
granted to: SuperAdmin (all), PlatformAdmin (read + manage — turning a customer's feature on is
day-to-day administration), SupportAgent (read — answering "why is POS off?"), BillingAdmin (read),
ReadOnly (read). The seeder syncs them; `SystemRoles` grows accordingly.

**PUT `/gyms/{id}/features` semantics** (approved): the request states desired values per feature
key. For each, if the desired value **equals what the plan (or default) would give**, any existing
override is **deleted**; otherwise an override is upserted with its required reason. The response is
the **full resolution**, not an echo — because after a clear, the stored state is deliberately not
what was sent. This keeps the §2.4 invariant true by construction and means a gym that later changes
plan follows the new plan instead of being frozen by a redundant override.

**`GET /gyms/{id}` gains its `features` block** (§3.3 always listed feature resolution in the gym
detail response), joining the `subscription` block M3.2 added.

## 6. Out of scope, stated

- **Enforcement.** Nothing in the platform *consumes* a resolved flag yet — the tenant-facing
  modules that would (POS, inventory, CRM) do not exist. M4 delivers the decision and its API; the
  first consumer enforces it. This is deliberate and worth stating so nobody assumes a disabled
  feature currently blocks anything.
- Per-feature usage analytics (M5 stats), the console UI (a GymOS-Platform increment), and any
  pricing rule tied to `is_premium`.

## 7. Verification plan

Unit: `FeatureResolution`'s truth table exhaustively (override on/off × plan on/off/absent ×
default on/off, and the no-subscription case); `Feature`/`GymFeatureOverride` invariants (immutable
key, required reason, no delete). Application: the clear-vs-upsert decision per case through the
real dispatcher with fakes, including "setting the plan's own value clears an existing override" and
"clearing when nothing is stored is a no-op success"; permission denial per route. Integration: the
seeder inserts twelve rows and **does not overwrite an edited default on restart** (the insert-only
rule, over real PostgreSQL); resolution end-to-end across all three layers; adoption counts;
`If-Match` required on `PUT /features/{id}`; the gym detail block. Live: seed, flip a plan feature,
override one gym, read both gyms' resolutions and see the sources differ.
