# Business Rules

One document per planned module, written **before** implementation so the rules are argued about while
they are still cheap to change.

**These are drafts, not specifications.** They record what the frozen architecture already decides,
what is assumed (marked `(?)`), and what is genuinely unanswered. The Golden Rule
(GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority: a rule listed here is not a
commitment to build the feature it belongs to.

| Module | Document | Notes |
|---|---|---|
| Members | [Members.md](Members.md) | Foundation for every gym-facing module. Health data needs a legal basis first. |
| Memberships | [Memberships.md](Memberships.md) | Entitlement rules; the most-asked-about module. |
| Payments | [Payments.md](Payments.md) | Least tolerance for a wrong rule. Idempotency is mandatory. |
| Scheduling | [Scheduling.md](Scheduling.md) | Already architecturally designed (AD-25); DST rules matter. |
| Attendance | [Attendance.md](Attendance.md) | Most exposed to offline and untrusted hardware clocks. |
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
