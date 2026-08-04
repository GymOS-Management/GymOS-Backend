<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# POS — Business Rules

**Module:** `GymOS.Modules.Pos` · schema `pos` · tenant-scoped
**Status:** not started.

## Purpose

The till at the front desk: selling products and services, taking payment, and closing the day. Used
under time pressure by staff who cannot stop to read an error message — which makes its failure modes
a usability requirement, not just a correctness one.

## Entities

| Entity | Notes |
|---|---|
| `Sale` | A transaction: lines, totals, tax, payment. Aggregate root. |
| `SaleLine` | Product or service, quantity, unit price, discount, tax. |
| `Discount` | Amount or percentage, with who authorized it. |
| `Till` | A physical point of sale, with its float. |
| `TillSession` | Open → close, with counted cash and the variance. |
| `Receipt` | The issued document; numbering is legally constrained. |

## Business Rules

| Code | Rule |
|---|---|
| `pos.sale_total_from_lines` | Totals are computed from lines with explicit rounding (AD-42), never sent by the client. A client-supplied total is a discount anyone can grant. |
| `pos.line_requires_price_snapshot` | Each line records the price and tax rate charged. |
| `pos.discount_requires_permission` | Above a threshold, a discount needs its own permission and is audited with who granted it. |
| `pos.sale_immutable_once_settled` | A correction is a refund plus a new sale. Editing a settled sale destroys the audit trail the accounts depend on. |
| `pos.void_before_settlement_only` | An unsettled sale can be voided; a settled one is refunded. |
| `pos.stock_reserved_then_committed` | Stock moves when the sale settles, not when a line is added — an abandoned sale must not consume stock. |
| `pos.till_session_required_for_cash` | Cash cannot be taken without an open till session, or it cannot be reconciled. |
| `pos.till_close_records_variance` | A counted-versus-expected difference is recorded and audited, never adjusted away. |
| `pos.receipt_number_sequential_no_gaps` | Sequential per gym per year, from a database sequence. **Gaps and duplicates are both a legal problem** — see Open Questions. |
| `pos.idempotent_settlement` | Settlement is an `IIdempotentCommand`: a double-tap at the till must not charge twice. |

## Invariants

- A settled sale's total equals the sum of its lines plus tax minus discounts, always recomputable.
- Every settled sale has at least one payment allocation covering its total.
- A sale references a member or is explicitly a walk-in.
- Receipt numbers are never reused, never edited, and never assigned outside the sequence.

## Future Rules

- Offline till operation and later sync (the gateway pattern applies).
- Split payments across methods.
- Selling a membership through the till.
- Returns without a receipt.
- Tips and staff commission.
- Fiscal printer or e-invoicing integration.

## Dependencies

- **Inventory** (stock), **Payments** (settlement), **Members** (the buyer), **Memberships** (if sold
  at the till), platform **Money**, **idempotency**, **audit**.
- **Events:** `gymos.pos.sale.settled`, `.voided`, `gymos.pos.till_session.opened`, `.closed`,
  `gymos.pos.discount.granted`.

## Open Questions

1. **Cyprus fiscal requirements**: receipt content, numbering, retention, and whether a certified
   fiscal device or e-invoicing is mandatory for this kind of retail. This constrains the schema, so it
   must be answered before implementation. **Verify with accounting and legal counsel.**
2. Does the till need to work offline in the MVP? Offline changes the architecture, not just the UI.
3. Discount authorization threshold — configurable per gym, presumably; who sets it?
4. Are services (personal training sessions) sold through POS or through Memberships?
5. Cash handling: is it in scope, and who is accountable for a variance?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
