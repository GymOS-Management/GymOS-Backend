# Subscriptions & Billing — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped**
**Status:** **M3.1 implemented** (plan catalogue). M3.2–M3.4 not started — see Scope below.

## Purpose

What the platform sells, and what each gym agreed to pay. The plan catalogue is the price list; a
subscription is one gym's contract against it; an invoice is a demand for money. Those are three
different lifetimes, which is why they are three increments.

## Scope of M3, and why it is four increments

| Increment | Contents | State | Prerequisite |
|---|---|---|---|
| **M3.1** | Plans, their limits and trial, and their prices | ✅ **built** | `Money` (AD-42) — done |
| **M3.2** | `gym_subscriptions`: assignment, trial, period, cancel-at-period-end | not started | M3.1 |
| **M3.3** | `invoices` + `invoice_lines`, mark-paid, void | not started | **AD-39 idempotency framework — not built.** "Marking an invoice paid" is the textbook qualifying command: a retried click must not pay twice. |
| **M3.4** | Renewal and overdue jobs | not started | **A job runner — not built.** Nothing in the platform yet runs work on a schedule. |

`plan_features` is also specified in §2.3 but belongs with **M4**: it references the `features` catalogue,
which does not exist. Building the join table first would give it nothing to point at.

## Entities (M3.1)

| Entity | Notes |
|---|---|
| `SubscriptionPlan` | Aggregate root. Platform-scoped, audited, **never deleted**. |
| `PlanPrice` | Part of the plan aggregate. One row per (cycle, currency). Holds a `Money`. |

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
| Subscribed-gym count on a plan | M3.2 | No subscriptions exist; the figure would always read zero, and a number that is always zero teaches operators to distrust the screen. |
| Feature set per plan (`plan_features`) | M4 | The `features` catalogue does not exist yet. |
| Invoices, mark-paid, void | M3.3 | Needs AD-39 idempotency: a retried "mark paid" must not pay twice. |
| Renewal and overdue jobs | M3.4 | Needs a job runner. |
| Multi-currency *pricing within one gym* | no business case | §3.2's scope guard: a gym has one operating currency. Several currencies on a *plan* is a different thing, and is supported. |
| Proration on plan change | M3.2 | Belongs with the assignment that triggers it, and needs `Money.Allocate` — which exists. |
| Tax on prices | M3.3 | A plan price is pre-tax. Tax rounding is per-jurisdiction configuration needing accounting counsel (AD-42/§3.2). |
