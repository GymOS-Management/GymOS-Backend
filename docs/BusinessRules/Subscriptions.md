# Subscriptions & Billing — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped**
**Status:** **M3.1 and M3.2 implemented** (plan catalogue, subscription assignment). M3.3–M3.4 blocked — see Scope below.

## Purpose

What the platform sells, and what each gym agreed to pay. The plan catalogue is the price list; a
subscription is one gym's contract against it; an invoice is a demand for money. Those are three
different lifetimes, which is why they are three increments.

## Scope of M3, and why it is four increments

| Increment | Contents | State | Prerequisite |
|---|---|---|---|
| **M3.1** | Plans, their limits and trial, and their prices | ✅ **built** | `Money` (AD-42) — done |
| **M3.2** | `gym_subscriptions`: assignment, plan change, trial, period, cancellation, payment state | ✅ **built** | M3.1 — done |
| **M3.3** | `invoices` + `invoice_lines`, mark-paid, void | not started | **AD-39 idempotency framework — not built.** "Marking an invoice paid" is the textbook qualifying command: a retried click must not pay twice. |
| **M3.4** | Renewal and overdue jobs | not started | **A job runner — not built.** Nothing in the platform yet runs work on a schedule. |

`plan_features` is also specified in §2.3 but belongs with **M4**: it references the `features` catalogue,
which does not exist. Building the join table first would give it nothing to point at.

## Entities

| Entity | Notes |
|---|---|
| `SubscriptionPlan` | Aggregate root. Platform-scoped, audited, **never deleted**. |
| `PlanPrice` | Part of the plan aggregate. One row per (cycle, currency). Holds a `Money`. |
| `GymSubscription` | Aggregate root. One **live** row per gym; ended rows are kept. Holds the agreed `Money` and cycle as a snapshot. |

## The price snapshot is a copy, not a foreign key

§2.3 calls `plan_price_id` the "price snapshot FK". **An FK cannot be the snapshot**, and the same
section is why: it makes `(plan_id, billing_cycle, currency)` unique, which leaves no room for price
versioning — so changing a plan's price *mutates that row*. A subscription that only pointed at it would
be silently repriced, the exact opposite of "renewals honor the price at assignment". A withdrawn price
would also dangle the reference.

So `GymSubscription` **copies** the amount, currency, and cycle at assignment, and keeps
`plan_price_id` as a nullable pointer for answering "which listed price did this come from" (cleared by
`ON DELETE SET NULL` if that row is withdrawn). Verified live: repricing a plan from €49.99 to €79.99
leaves an existing subscription paying €49.99, and a *new* subscription assigned afterwards pays €79.99.

## Subscription lifecycle

`Trialing → Active ⇄ PastDue`, with `Canceled` and `Expired` as terminal states, enforced by
`SubscriptionStatusTransitions` and covered as an exhaustive matrix.

- **Canceled and Expired are both terminal and both exist.** Cancellation is a decision, which is what a
  churn figure counts; expiry is time running out, usually a failure to collect. Collapsing them would
  make the M5 churn metric wrong in a way nobody could later untangle.
- **A terminal subscription is never revived.** A gym that returns gets a new row, leaving the old one as
  the record of what it actually had — the same reasoning as a deleted gym.
- **`PastDue` does not withhold service.** Whether a late-paying gym keeps operating is a separate
  decision, taken by suspending the *gym*. Every failed card must not end a business's day.
- **Scheduled cancellation is not cancellation.** `cancelAtPeriodEnd` leaves the subscription `Active`
  and live: the gym paid to the end of the period. Treating "asked to cancel" as "cancelled" would cut
  off a paying customer, which is the most expensive kind of billing bug.
- **Changing plan updates the one subscription** rather than ending it and starting another. The status
  enum has no state for "replaced", so recording an upgrade as a cancellation would inflate churn.
- **A trial is a grace on payment, not a pause on the calendar.** A monthly plan with a 14-day trial
  still renews a month in, which is what the gym's first invoice will say.
- **Trial conversion, renewal, and expiry are explicit operations for now.** The renewal job (M3.4) will
  drive them from dates. Until then a trial that has run out stays `Trialing` with `trialEndsAtUtc` in
  the past — legible rather than wrong — and `TrialHasEnded`/`PeriodHasEnded` are derived, never stored,
  so no job is needed to keep them true.

## Business Rules

| Code | Rule |
|---|---|
| `plans.code_invalid` | A plan's code is an upper-case identifier (`PRO`, `PRO_ANNUAL`): 2–40 characters, letters, digits, single underscores. It leaves the system — exports, accounting integrations, support conversations — so it cannot contain spaces or punctuation. Lower case is accepted on input and upper-cased; it is not a mistake to type `pro`. |
| `plans.code_taken` | Codes are unique. Two plans sharing one would make every downstream mapping ambiguous. |
| `plans.name_invalid` | A name is required, at most 120 characters. Unlike the code, it is free prose and free to change. |
| `plans.trial_days_invalid` | 0–365 whole days. Zero means no trial, which is a decision; there is no null. |
| `plans.limit_invalid` | A stated limit must be positive. **Null means unlimited** — zero would mean "none allowed", and `int.MaxValue` makes every limit check read as arithmetic instead of as a question about whether a ceiling exists. |
| `plans.price_invalid` | A price must be greater than zero. A free tier is expressed by having *no* price row for a cycle, not by a €0.00 line — which reads as a billing failure to whoever receives the invoice. |
| `plans.billing_cycle_invalid` | `Monthly`, `Quarterly`, or `Yearly`, matched case-insensitively but never guessed. Billing a gym annually for a monthly plan is not a mistake worth being lenient about. |
| `plans.currency_invalid` | A three-letter ISO 4217 code. It decides how the amount is rounded, so an unrecognised one cannot be defaulted. |
| `plans.already_active` / `plans.already_inactive` | Withdrawing or restoring twice is reported rather than silently succeeding. |
| `plans.price_not_found` | A price id that does not belong to this plan. |
| `subscriptions.price_not_offered` | The plan has no price for the requested cycle and currency. Refused rather than approximated — picking "the nearest price" would bill an amount nobody agreed to. |
| `subscriptions.plan_inactive` | A withdrawn plan cannot be **newly** assigned. A gym already on one keeps it and can still have its terms adjusted (§2.3), or withdrawing a plan would strand customers who then could not be corrected. |
| `subscriptions.gym_deleted` | A deleted gym cannot be subscribed; there is nothing left to bill. A **suspended** gym can be — resolving a billing dispute often *is* fixing the subscription. |
| `subscriptions.not_found` | The gym has no live subscription. A normal state, not an error condition: a gym can exist before it is sold anything. |
| `subscriptions.cancellation_already_scheduled` / `no_cancellation_scheduled` | Reported rather than silently accepted. |
| `subscriptions.not_trialing` / `not_past_due` / `already_past_due` | Wrong-state transitions are reported, so a caller that has lost track of state is told. |

## Invariants

- **A plan is never deleted.** §3.4 is explicit: "delete = deactivate only, never destroys history".
  Subscriptions and invoices reference plans, and destroying one would orphan the record of what a gym
  agreed to pay. `DELETE /plans/{id}` returns **405**, asserted as a property of the API surface.
- **`Code` is immutable after creation**, for the same reason a gym's slug is: it leaves the system.
- **One price per (plan, billing cycle, currency)** — enforced in the aggregate *and* by a unique index.
- **Prices are rows, not columns.** Offering annual billing or a second currency is data entry.
- **A price change never reprices an existing subscription.** Subscriptions snapshot the price they were
  assigned (§2.3), so changes apply to new assignments and renewals. This is also why removing a price is
  safe: it withdraws an offer, it does not rewrite a contract.
- **`IsActive` and `IsPublic` are different questions.** Inactive means "cannot be newly assigned";
  unlisted means "assignable but not advertised" — a negotiated plan. Conflating them would make every
  private deal invisible to the person who agreed it.
- **Billing-cycle enum values are month counts** (Monthly=1, Quarterly=3, Yearly=12), so a period's
  length is the value itself. They are stored values: renumbering them would reprice every subscription.
- **Money is never a naked decimal** (R4, now enforced). The one exception is a serialisation boundary —
  event payloads and API DTOs carry an amount beside an explicit currency code, because `Money` has no
  defined wire form yet.

## Storage notes

- `plan_prices.price_amount numeric(14,4)` + `price_currency varchar(3)` — the shared `Money` mapping.
  §2.3 specifies `numeric(12,2)` for this column; 14,4 is wider on both axes, so every value the spec
  allows still fits, and one money shape across the platform beats a per-table precision decision.
- The **(plan, cycle, currency) unique index is raw SQL** in the `AddSubscriptionPlans` migration: EF
  cannot build an index across a complex type's member. Isolated and documented there; any future change
  to the price key must be made the same way.
- `code` carries a `code = upper(code)` CHECK, so the unique index keeps meaning "one plan per code".

## Deferred, with reasons

| Deferred | Until | Why |
|---|---|---|
| ~~Subscribed-gym count on a plan~~ | ~~M3.2~~ | **Done** — `subscribedGymCount` counts gyms on a live subscription, and the gym detail view now carries its `subscription`. Both were deliberately omitted while they could only have read zero or null. |
| Feature set per plan (`plan_features`) | M4 | The `features` catalogue does not exist yet. |
| Invoices, mark-paid, void | M3.3 | Needs AD-39 idempotency: a retried "mark paid" must not pay twice. |
| Renewal and overdue jobs | M3.4 | Needs a job runner. |
| Multi-currency *pricing within one gym* | no business case | §3.2's scope guard: a gym has one operating currency. Several currencies on a *plan* is a different thing, and is supported. |
| Proration on plan change | **M3.3**, corrected from M3.2 | Charging or crediting the difference needs an invoice to land on. `Money.Allocate` is ready; there is nowhere to put the result until invoices exist, so changing plan leaves the paid period alone. |
| Tax on prices | M3.3 | A plan price is pre-tax. Tax rounding is per-jurisdiction configuration needing accounting counsel (AD-42/§3.2). |
