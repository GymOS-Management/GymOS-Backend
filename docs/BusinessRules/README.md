# Business Rules

One document per planned module, written **before** implementation so the rules are argued about while
they are still cheap to change.

**Members, Memberships, and Attendance are now specified** in
[`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`](../Domain/MEMBERS-DOMAIN-DESIGN.md) — the authoritative
business design for that domain, including the Open Product Decisions that block M2 implementation.
Their entries below remain as the questions that fed it.

**Gyms is implemented** — [`Gyms.md`](Gyms.md) records the rules M2 actually enforces, including what
was deliberately deferred to M3–M6.

**The subscription plan catalogue is implemented** — [`Subscriptions.md`](Subscriptions.md) records M3.1
as built, and why invoices and renewal jobs wait on the idempotency framework and a job runner
respectively.

**Invoices are implemented** — [`Invoices.md`](Invoices.md) records M3.3–M3.4 as built: the three
creation paths, the plan-change decision tree, and what waits on accounting counsel.

**Feature flags are implemented** — [`Features.md`](Features.md) records M4 as built: the three-layer
resolution rule, the twelve keys, and why an override is stored only while it differs from the plan.

**Analytics & dashboard are implemented** — [`Analytics.md`](Analytics.md) records M5 as built: the
live-versus-recorded split, MRR's exact definition, why churn keeps its two causes apart, and every
column and endpoint deferred for lack of a real source.

**Settings & hardening are implemented** — [`Settings.md`](Settings.md) records M7 as built: the
24-key declared catalog resolved row → configuration → default, sensitive values under Data
Protection with the key ring in the database until Key Vault, a confirmed maintenance-mode toggle, a
global rate limiter tuned live from the same catalog, and the audit trail's CSV export.

**The rest are drafts, not specifications.** They record what the frozen architecture already decides,
what is assumed (marked `(?)`), and what is genuinely unanswered. The Golden Rule
(GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority: a rule listed here is not a
commitment to build the feature it belongs to.

| Module | Document | Notes |
|---|---|---|
| Gyms | **[Gyms.md](Gyms.md)** | **Implemented (M2).** A specification, not a draft: it records the rules as built. |
| Subscriptions & Billing | **[Subscriptions.md](Subscriptions.md)** | **Plan catalogue implemented (M3.1).** Records why M3 is four increments and what each waits on. |
| Invoices | **[Invoices.md](Invoices.md)** | **Implemented (M3.3–M3.4).** Issued as a consequence, never on request; numbering and tax are flagged for counsel. |
| Features | **[Features.md](Features.md)** | **Implemented (M4).** `override ?? plan ?? platform default`, with the layer that decided it reported alongside the answer. Nothing enforces a flag yet. |
| Analytics & Dashboard | **[Analytics.md](Analytics.md)** | **Implemented (M5).** Dashboard reads live, analytics reads a once-a-day read model; MRR is per-currency, never summed; churn keeps cancellations and expiries apart. |
| Members | **[Domain design](../Domain/MEMBERS-DOMAIN-DESIGN.md)** · [draft](Members.md) | **Specified.** Health data, minors, and retention need legal counsel. |
| Memberships | **[Domain design](../Domain/MEMBERS-DOMAIN-DESIGN.md)** · [draft](Memberships.md) | **Specified.** Freeze policy and visit definition are blocking decisions. |
| Payments | [Payments.md](Payments.md) | Least tolerance for a wrong rule. Idempotency is mandatory. |
| Scheduling | [Scheduling.md](Scheduling.md) | Already architecturally designed (AD-25); DST rules matter. |
| Attendance | **[Domain design](../Domain/MEMBERS-DOMAIN-DESIGN.md)** · [draft](Attendance.md) | **Specified.** Offline conflicts are surfaced, never resolved silently. |
| Inventory | [Inventory.md](Inventory.md) | Serves POS. May not be MVP. |
| POS | [POS.md](POS.md) | Fiscal/receipt law constrains the schema — answer that first. |
| Trainer | [Trainer.md](Trainer.md) | Golden Rule bites hardest: must beat paper. |
| Nutrition | [Nutrition.md](Nutrition.md) | Most likely to be cut. Health data and professional regulation. |
| CRM | [CRM.md](CRM.md) | Consent law is the hard part, not the domain model. |
| Reports | [Reports.md](Reports.md) | Every report names the decision it informs, or is not built. |

## Recurring themes

Worth reading once rather than eleven times:

- **Derive, do not store.** Entitlement balances, occupancy, stock on hand, outstanding charges, booked
  counts — every one is derived from a ledger of movements. A stored counter and its ledger will
  disagree, and the disagreement is always discovered by a customer.
- **Immutability where money or history is involved.** Settled payments, sales, check-ins, and progress
  entries are corrected by compensating records, never edited.
- **Price and rate snapshots.** A transaction records what was charged and at what rate, so changing a
  price never rewrites history.
- **Idempotency is not optional** for anything that charges, books, unlocks, or registers (R5, AD-39).
- **State machines, not boolean flags** — the pattern `UserStatusTransitions` already sets.
- **Legal questions block schema decisions**, not just features: fiscal receipts, health data, and
  marketing consent all shape tables. They belong in the scoping conversation, and each needs
  confirmation from legal or accounting counsel before implementation.
