# Features — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (a feature is a decision the
platform makes *about* a gym, not data belonging to one)
**Status:** **implemented — M4, then revised by M4.1's review-fixes increment.** This document records
the rules as built, including M4.1's corrections to what M4 originally claimed.
Design: [`docs/superpowers/specs/2026-08-11-feature-flags-design.md`](../superpowers/specs/2026-08-11-feature-flags-design.md)
(M4) and [`docs/superpowers/specs/2026-08-12-m4-review-fixes-design.md`](../superpowers/specs/2026-08-12-m4-review-fixes-design.md)
(M4.1).

## Purpose

What each gym can do, and **why**. A feature flag answers a support question as often as a technical
one — "is POS on for this gym?" and "why is POS on for this gym?" are the same conversation — so
every answer this module gives carries the layer that decided it.

**Nothing consumes a resolved flag yet, deliberately.** The tenant-facing modules that would enforce
one — POS, inventory, CRM — do not exist. M4 delivers the decision and its API; the first consumer
enforces it. A disabled feature blocks nothing today, and assuming otherwise would be a security
mistake, not a small one.

## The rule, in one line

`override ?? plan ?? platform default` — the first layer with an opinion wins.

| # | Layer | Source | Applies when |
|---|---|---|---|
| 1 | Gym override | `gym_feature_overrides` row | One exists — a deliberate, reasoned, authored exception |
| 2 | Plan feature | `plan_features` row for the gym's **live** subscription's plan | No override, and the plan has an opinion on this feature |
| 3 | Platform default | `features.default_enabled` | Nothing above said anything |

`FeatureResolution.Resolve` is the **single authoritative definition** of that sentence: a pure,
total function of three nullable inputs, so the rule is exhaustively testable without a database and
cannot drift between the endpoints that ask it. The write path and the per-gym read
(`ResolveForGymAsync`) both call it directly.

**One deliberate exception: `GET /features`' `adoptionCount` restates the rule in SQL.** Counting how
many gyms resolve a feature *on* cannot be done by calling `Resolve` in C# without loading every gym
into memory — the exact cost `FeatureQueries.Project()`'s own comment records rejecting an earlier,
cleverer version over ("correct-looking and unverifiable"). So the `override ?? plan ?? default`
chain is written a second time, as a translated LINQ expression, next to a comment naming it as the
restatement it is. `FeatureAdoptionParityTests` is what keeps that copy honest: it builds a matrix of
gym states and asserts the SQL count agrees with calling `FeatureResolution.Resolve` per gym, in C#,
over the same states — because the deferred override-expiry work (below) adds a fourth input to
`Resolve`, and without a pinning test the SQL copy could silently omit it.

**A gym with no live subscription resolves at layer 3.** A gym is provisioned before it is sold
anything — M2 supports exactly that order — so a provisioning gym stays configurable and demoable,
and a cancelled gym degrades to the platform baseline instead of going dark.

## Entities

| Entity | Notes |
|---|---|
| `Feature` | Aggregate root. Platform-scoped, audited, `row_version` concurrency token. Key **immutable**; copy, default, and premium flag are operator-owned. **Never created or deleted through the API.** |
| `GymFeatureOverride` | Aggregate root. Platform-scoped, audited, `row_version`. Unique on `(gym_id, feature_id)`. `Reason` **required**; author and timestamp recorded automatically. |
| `PlanFeature` | Part of the `SubscriptionPlan` aggregate, reached through the plan exactly as `PlanPrice` is. Exempt from the concurrency-token rule for `PlanPrice`'s recorded reason: it is mutated only through the plan, whose token guards it (`MarkAggregateChanged`). |

## The catalogue is code-canonical, its copy is not

`PlatformFeatures.All` declares the twelve keys §2.4 names; a startup seeder inserts the ones the
database does not have. **No endpoint creates or deletes a feature** — a key is a compile-time
constant that code branches on, so one invented at runtime would be a flag nothing reads, and a
deleted one would strand the code reading it (and cascade into plan contents and gym overrides).
The catalogue grows in a reviewed commit, exactly like `PlatformPermissions`.

**But the seeder is insert-only, and that is the one place this catalogue differs from permissions.**
`name`, `description`, `category`, `default_enabled` and `is_premium` are editable at runtime (§3.5),
so reconciling them on boot would silently revert a deliberate console edit — the worst kind of bug,
because the operator's change works until someone restarts the host. The declaration supplies the
*opening* values for a key nobody has seen; after that the operator owns the row.

A row whose key no longer appears in code is **logged, never deleted**.

## The twelve keys, and why each default is what it is

The defaults say: *the operational core is on for everyone; anything a customer would pay extra for,
or that needs hardware or an AI budget, is off until it is sold.* They are an opening position, not a
law — every one is editable at runtime, and the seeder will not argue.

| Key | Category | Default | Premium | Why this default |
|---|---|---|---|---|
| `pos` | Operations | **on** | no | A gym that cannot take a payment at the desk is not running. |
| `inventory` | Operations | **on** | no | Serves POS; stock is part of operating, not an upsell. |
| `reports` | Insights | **on** | no | A gym must be able to see its own numbers. |
| `crm` | Members | off | yes | Real value, real cost to build — off until a plan includes it. |
| `nutrition` | Training | off | yes | Sold capability. |
| `workout_builder` | Training | off | yes | Sold capability. |
| `member_app` | Members | off | yes | Sold capability. |
| `marketing` | Growth | off | yes | Sold capability. |
| `analytics` | Insights | off | yes | Beyond the standard reports every gym gets. |
| `ai_assistant` | Growth | off | yes | Costs money **per use**; on-by-default would spend a budget nobody agreed. |
| `gateway` | Integrations | off | no | Needs hardware on site — enabling it for a gym that has none promises what the platform cannot deliver. Not premium: it is a prerequisite, not an upsell. |
| `access_control` | Integrations | off | no | Same: doors and turnstiles have to exist. |

`Category` is a display grouping for the console's catalogue list. **It is not behavior**, and nothing
branches on it.

**`is_premium` gates nothing** (approved decision). It is carried and displayed. Gating overrides on
it was considered and rejected: it would remove the operator's ability to grant a trial or a goodwill
exception, which is the main reason overrides exist. Any stronger meaning is a pricing rule nobody
has specified.

## An override is withdrawn once it stops differing — immediately for its own gym, within a day otherwise

The invariant §2.4 states is enforced by construction, but **it is not a single instant any more, and
this section says so rather than continuing to claim one.**

> **A redundant override — one whose value equals what the gym would get anyway — is withdrawn
> immediately when the change is to that gym**, and **within a day when the change is to a plan or a
> platform default.**

The first half was M4's whole claim, and it is still true at a `PUT /gyms/{id}/features` write:
asking for exactly what the plan — or the platform default, when the plan is silent — already gives
**withdraws** any existing exception instead of restating it, in the same request. It is also true
whenever *this gym's own subscription* changes — assigned a plan, changed plan, or its subscription
ending — because that write re-evaluates the gym's exceptions in the same transaction
(`GymFeatureExceptions.Redundant`, the same `FeatureResolution.RequiresOverride` rule the write path
already used, applied to existing rows rather than only the one being written). This covers
`RenewalJob`'s expire-and-cancel steps too, not only an operator-driven API call.

**The M4 review found the second half missing.** Three other triggers make an override redundant
without touching the gym at all: a plan's feature opinion changing (`PUT /plans/{id}/features`,
every gym on that plan) and a platform default flipping (`PUT /features/{id}`, every gym with an
override on that feature). Doing those synchronously was considered and rejected: editing one plan
feature would rewrite overrides for every gym on the plan inside the request, turning a quick billing
edit into an unbounded write, and coupling a billing action to feature rows the design deliberately
kept apart. Instead, **`FeatureOverrideSweepJob` runs nightly at 03:50 UTC** (after `RetentionJob`'s
03:10 and `PlatformStatsJob`'s 03:30, M3.4's job runner, under its `IJobLock`) and re-evaluates every
gym holding at least one override — a small population by construction. Withdrawal raises the
existing `GymFeatureOverrideCleared` event, so the audit trail records it exactly as an
operator-driven withdrawal. The job is built as "re-evaluate this gym's exceptions", not "delete
redundant rows", because override **expiry** (still deferred, below) is one more predicate in the
same pass — building the hook any other way means building it twice.

Why it matters: a redundant override is a row nobody remembers creating, and it **freezes the gym at
its old plan's answer**. A gym upgraded a year later would keep the exception and silently not get
what it now pays for; a gym downgraded a year later would keep a capability it no longer buys. Between
a gym-scoped change and the next sweep, that is exactly the state a support conversation might hit —
`source: Override` reported truthfully, but for at most a day before the layers beneath quietly catch
up. Clearing is the correct response to "set this to what it already is", not a no-op, and the
guarantee is worded to say precisely when that response runs.

Three consequences worth stating:

- **`reason` is required only when an override is genuinely needed.** Withdrawing one needs no
  justification — the gym is returning to what it pays for.
- **The response is the full resolution, not an echo.** After a withdrawal the stored state is
  deliberately not what was sent, and echoing the request back would hide that. It is also the answer
  the caller wanted: *is it on, and why*.
- **Only the keys sent are touched.** A client that knows about ten features cannot silently withdraw
  an exception on an eleventh added by a later release.

**Validation runs entirely ahead of mutation.** One unknown key, one duplicated key, or one missing
reason refuses the **whole** request: a caller who cannot tell which half of their intent landed is
worse off than one who was refused. The reason check is duplicated in the handler rather than caught
out of the aggregate, so a five-key request cannot write three before discovering the fourth is
unjustified — the domain rule stays the guard of last resort.

**A deleted gym cannot be configured** (`gyms.deleted`); a **suspended** one can, deliberately — an
operator often adjusts what a gym gets *while* it is suspended, before letting it back in.

## What a plan says, and the third state

A `plan_features` row is an **opinion**; its absence is **silence**. `isEnabled: null` on
`PUT /plans/{id}/features` clears the plan's opinion, which is a different answer from `false`:

- *"This plan does not include CRM"* (`false`) — gyms on it never get CRM, whatever the platform does.
- *"This plan has no view on CRM"* (absent) — gyms on it follow the platform default, and follow it
  again if that default changes.

They look identical to a gym today and answer differently the moment a default moves. That third
state is why resolution has three layers rather than two.

## Permissions: two vocabularies, deliberately split

| Permission | Covers |
|---|---|
| `features.read` | The catalogue, and any gym's resolved features |
| `features.manage` | Editing the catalogue, and setting or clearing a gym's overrides |
| `billing.read` / `billing.manage` | **What a plan includes** — `GET`/`PUT /plans/{id}/features` |

The split is the point: **granting one gym an exception is day-to-day administration; deciding what a
plan includes is pricing the product.** Support staff should be able to do the first without holding
the authority to redefine what the platform sells.

Grants (seeded, reconciled at startup): SuperAdmin all · PlatformAdmin read + manage · SupportAgent
read (answering "why is POS off?") · BillingAdmin read (it edits plan contents through `billing.*`) ·
ReadOnly read.

## Endpoints

| Method | Route | Permission | `If-Match` | Notes |
|---|---|---|---|---|
| GET | `/api/v1/features` | `features.read` | — | Catalogue with `adoptionCount` and `overrideCount`. Not paged — the catalogue is seeded. |
| PUT | `/api/v1/features/{id}` | `features.manage` | **required** | Copy, `defaultEnabled`, `isPremium`. Key not editable. |
| GET | `/api/v1/plans/{id}/features` | `billing.read` | — | Returns the plan (features are part of it). |
| PUT | `/api/v1/plans/{id}/features` | `billing.manage` | **required** | `true` / `false` / `null` = clear. |
| GET | `/api/v1/gyms/{id}/features` | `features.read` | — | The **whole catalogue** resolved, with `source` and `overrideReason`. |
| PUT | `/api/v1/gyms/{id}/features` | `features.manage` | **none** — see below | Sets or withdraws overrides; returns the full resolution. |

`GET /api/v1/gyms/{id}` carries the same resolved block as `features`, which is what §3.3 always
described. `GymQueries` delegates to `IFeatureQueries` rather than resolving a second time.

**The block is omitted, not gated, when the caller lacks `features.read` (M4.1 task 4).** Gym detail
sits behind `gyms.read` alone; before this increment that meant a `gyms.read`-without-`features.read`
role — buildable today through `roles.manage`, though no stock role does it — bypassed the split this
document calls "the point", reading the full resolution including the free-text `overrideReason`
through a permission that was never meant to grant it. The route still needs only `gyms.read`; the
`features` field becomes optional in the response and is simply absent when the caller cannot read
features, on all six write routes that return a gym detail as well as the direct `GET`. Requiring
both permissions on the route was considered and rejected: it would take a usable gym page away from
a `gyms.read`-only role over one field, where omitting the field costs nothing and serves least
privilege instead.

**`adoptionCount` counts gyms that resolve the feature *on* through all three layers**, not plan rows.
A feature can be live because of an override, because of a plan, or because of the default; a figure
that counted only one of those would understate reality and mislead the decision it exists to inform
("can we retire this?"). `overrideCount` is how many gyms hold an explicit exception.

### Where `If-Match` is required, and where it is not

**Required on the two catalogue-and-plan writes.** Both overwrite what the caller read with no state
machine to refuse a stale interleaving — the M3.1 price lesson from the whole-repository audit,
applied here from the start. Flipping `defaultEnabled` has the widest blast radius of any write in
the module: it re-answers the feature for every gym that has neither an override nor a plan opinion.

**Not required on `PUT /gyms/{id}/features`**, and that is a considered difference rather than an
omission. There is no single version to match: each override is its own aggregate keyed on
(gym, feature), and the request merges per key, so two operators editing *different* features do not
conflict at all. Two editing the *same* one is a disagreement about policy, not a lost update — the
later decision wins, carries its own required reason, and the audit trail keeps the earlier one.

### Two concurrency guards behind `PUT /gyms/{id}/features`, not visible in the contract

**A repeated identical grant retries once instead of 409ing.** Two callers granting the same
(gym, feature) exception at the same moment both see "no existing override" and both insert; the
unique index lets exactly one through. The loser is not told to retry with a different value — there
is no other value — it is retried once, internally, which finds the winner's row and restates it.
Only a second, unrelated conflict past that point surfaces as `persistence.uniqueness_conflict` (409).

**The decision is made against the gym's live plan, locked for the length of the write.** The
handler decides whether a request needs an override by reading what the gym's plan currently says;
that read takes the plan row `FOR UPDATE` and holds it in one transaction through the save *and* the
read-back that builds the response — not just through the save. A concurrent
`PUT /plans/{id}/features` on the same plan blocks until that transaction ends, so it can never change
the plan's opinion in the gap between this write's decision and the body it returns: the response is
always exactly the resolution this transaction is committing, never one already superseded by the time
it is read.

### The same lock is missing on the subscription side — a known, narrow gap

**`PUT /gyms/{id}/subscription` (`AssignSubscriptionCommandHandler`) reads the plan without the guard
above.** It opens no explicit transaction and calls `IPlanRepository.FindByIdAsync`, not
`FindByIdForUpdateAsync`, before deciding — via the synchronous `WithdrawRedundantAsync` call this
increment added — which of the gym's stored exceptions the new plan makes redundant.

**The interleaving that loses a capability.** T1 assigns a gym to `PRO`, reading `crm = true`. Before
T1 commits, T2 commits `PUT /plans/PRO/features` setting `crm = false`. T1's maintainer, judging
against the stale `crm = true` it already read, decides the gym's `crm = true` override is now
redundant (the assignment appears to grant the same thing) and withdraws it. Final state: the gym is
on `PRO`, which now says `crm = false`, and the exception that was protecting `crm` is gone. Withdrawal
is a hard delete (`RemoveOverride`), not a soft one, so the next nightly sweep does not put it back —
there is nothing left for the sweep to find redundant or not.

**The loss is a missing capability, not corrupted data.** Nothing is stored incorrectly: the gym
genuinely has no override and its plan genuinely says `crm = false`, which is a state
`GET /gyms/{id}/features` reports truthfully. What is lost is the *decision* — the override an operator
deliberately granted — and the only trace of it is the outbox: the withdrawal still raises
`GymFeatureOverrideCleared` exactly as an operator-driven clear does, so an operator who goes looking
finds the moment it disappeared and can re-grant it. It does not come back on its own.

**How narrow this is.** Both writes must race — `PUT /plans/{id}/features` on the exact plan a gym is
mid-assignment to or mid-change onto, inside the few milliseconds between this handler's plan read and
its commit — and the outcome must specifically be the withdrawal branch of `GymFeatureExceptions.Redundant`
finding a now-stale match. Every other interleaving converges correctly: if the plan edit lands first or
after this transaction's commit, the maintainer sees one consistent state or the other, never a mix; if
the assignment does not touch a feature the plan edit changed, nothing depends on the stale read; and
the `PUT /gyms/{id}/features` write path this section describes above is already immune, because it
takes the same lock the assignment path is missing.

**Not fixed here, deliberately.** The fix is the same shape already proven on
`SetGymFeaturesCommandHandler`: an explicit transaction plus `FindByIdForUpdateAsync` around the plan
read in `AssignSubscriptionCommandHandler`, so a concurrent `PUT /plans/{id}/features` on the same plan
serialises behind it exactly as it already does for the gym-features write. That touches the busiest
handler in the subscriptions slice — every assignment, upgrade, downgrade, and cycle change — for a
race two writes this specific have to hit, so it is recorded rather than folded into this wave. Tracked
in `docs/STATUS.md`'s known open items.

## Value rules and stable codes

| Code | Kind | When |
|---|---|---|
| `features.key_invalid` | Validation | A key that is not lower-case `snake_case`, or over 60 characters (seed-time guard; no endpoint accepts a new key) |
| `features.name_invalid` | Validation | Name missing, blank, or over 120 characters |
| `features.override_reason_required` | Validation | A value that genuinely needs an override was sent without a reason, or with one over 500 characters. Names the key that lacks it |
| `features.unknown_key` | Validation | A key that is not in this platform's catalogue |
| `features.duplicate_key` | Validation | The same key stated twice in one request |
| `features.invalid` | Validation | A domain rule refused a catalogue edit |
| `features.not_found` | NotFound | No such feature id |
| `concurrency.version_required` | Conflict (428) | `If-Match` missing on a write that requires it |
| `concurrency.malformed_version` | Validation | `If-Match` is not a token this API issued |

The same key twice is **refused**, not applied last-one-wins: there is one row per (gym, feature), so
applying the last would honour half a self-contradicting request while reporting success — and it
would otherwise surface as an unexplained 409 from the unique index.

## Events

| Event | Raised when |
|---|---|
| `gymos.platform.feature.default_changed` | A platform default flips — worth an event rather than a silent column write, because it re-answers the feature for every gym that never decided for itself |
| `gymos.platform.gym_feature.overridden` | An exception is granted or restated; carries the reason and the author |
| `gymos.platform.gym_feature.override_cleared` | An exception is withdrawn — including when an operator sets a value that happens to equal what the plan already gives |

There is no consumer yet (the outbox processor is capture-side only). The events exist so the first
one does not have to change the aggregates.

## Persistence

Migration `AddFeatures`: `platform.features` (unique `key`), `platform.plan_features` (unique
`(plan_id, feature_id)`, FK plan → cascade like `plan_prices`, FK feature → restrict),
`platform.gym_feature_overrides` (unique `(gym_id, feature_id)`, FK gym → cascade — an override is
meaningless without its gym; FK feature → restrict; FK `set_by` → users, `SetNull`, provenance only,
the `PlanPriceId` precedent). All three join `PlatformAuditedEntities`: **who gave a gym a paid
feature for free** is exactly what a later billing conversation turns on.

## `Feature.Rename` was designed; `Feature.Describe` is what got built

The M4 design named the catalogue-edit method `Feature.Rename`. The aggregate that shipped calls it
`Describe(name, description, category)` — it edits three fields together, not one, and "rename"
undersold what §3.5 actually lets an operator change. Recorded here as a deliberate naming deviation
from the approved design rather than left for a reader to notice the two disagree.

## M4.1 — what this increment fixed, and what remains deferred

M4's headline invariant ("an override is stored only while it differs") was true only at the instant
of a `PUT /gyms/{id}/features` write; nothing maintained it afterwards. This increment built the
maintenance: a gym's own subscription changing withdraws its now-redundant exceptions **synchronously**
(the section above), and `FeatureOverrideSweepJob` withdraws the ones a plan-feature change or a
platform-default flip makes redundant **within a day**. It also closed the plan-edit race on
`SetGymFeaturesCommandHandler` (the plan row is now read `FOR UPDATE` in the same transaction), fixed
two seeders (`FeatureCatalogSeeder`, `PlatformIdentitySeeder`) that could crash the host on a
concurrent first boot, gave `PUT /gyms/{id}/features` a single retry so a repeated identical grant
converges instead of 409ing, made `PUT /plans/{id}/features` refuse `{}`/null instead of 500ing and
refuse a repeated key instead of applying it last-one-wins, and added the three contract fields a
console needs (`isDeclared`, the override's `overrideSetAtUtc`/`overrideSetByEmail`, and the
`gyms.read`-only-safe optionality of `GET /gyms/{id}`'s features block). It also corrected the
documentation above: resolution's one deliberate SQL restatement, now pinned by
`FeatureAdoptionParityTests`, and the invariant's true two-schedule wording.

**Left deferred, on purpose, and not part of this increment:**

| Topic | Where it went |
|---|---|
| **Enforcement** — anything actually consuming a resolved flag | The first tenant-facing module that has a capability to withhold (POS, inventory, CRM) |
| **An expiring override** — see below | Its own increment; needs a product decision first |
| **`PlanQueries.GetAsync`'s four correlated subqueries per plan-feature row** | Bounded at 12 × 4 today; rewriting it earns nothing until the catalogue grows |
| **`FeatureRepository.ListAllAsync` being tracked** | Twelve rows into the change tracker for a key lookup — harmless now, recorded so it is not forgotten |
| **`LogSynced` counting orphans inside "left untouched"** | True but conflated; cosmetic |
| A pricing rule tied to `is_premium` | Needs a specified commercial rule; today it is informational |
| Per-feature usage analytics | M5 stats |
| The console UI for all of this | A GymOS-Platform increment |
| Bulk "apply to every gym on this plan" | No caller has needed it; the plan layer already does it without a write per gym |

### An override never expires — and a trial is the reason overrides exist

Raised by the M4 review, deliberately **not** fixed inside it, and still open after M4.1: the sweep
this increment built withdraws an exception once it stops differing from the plan or default, but
nothing withdraws one that simply runs past a time limit, because no override carries one. An
override records who granted it, when, and why, but nothing time-bounded, so **a goodwill trial
granted this way is permanent until a human remembers it**. That is the headline use case for the
feature, and it currently has no ending.

There is no live consequence while nothing consumes a resolved flag — but the day enforcement lands,
this is revenue quietly leaking through the mechanism built to hand out favours.

Fixing it properly is a product decision before it is a schema one: what should happen at expiry —
does the exception simply lapse back to the plan, or does someone get told first? An expiring
override that silently switches a customer's POS off is a worse failure than one that never expires.
The mechanism, once that is settled, is small: an `ExpiresAtUtc` on the override plus one more
predicate in `FeatureOverrideSweepJob`'s existing nightly pass — the job was deliberately built as
"re-evaluate this gym's exceptions" rather than "delete redundant rows" for exactly this reason, so
expiry is an addition to a pass that already exists, not a second mechanism.
