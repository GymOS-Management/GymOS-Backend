<!--
  DRAFT — not an approved specification.

  This document collects what is known and what is undecided about a module that has not been
  built. It is a starting point for the scoping conversation, not a commitment: the Golden Rule
  (GymOS-Specification/PRODUCT-PRINCIPLES.md) is the scope authority, and every rule below must be
  confirmed with the product owner before it is implemented. Rules marked (?) are assumptions.
-->

# Payments — Business Rules

**Module:** `GymOS.Modules.Payments` · schema `payments` · tenant-scoped
**Status:** not started. The module with the least tolerance for a wrong rule.

## Purpose

Money owed and money received, for gym-side transactions: membership charges, class fees, POS sales,
refunds. Distinct from the platform's own billing of gyms (`platform.invoices`).

Two properties matter more than features: **every figure is reconstructable**, and **nothing charges
twice**.

## Entities

| Entity | Notes |
|---|---|
| `Charge` | An amount owed, with what it is for. |
| `Payment` | An amount received, with how. Immutable once settled. |
| `PaymentAllocation` | Which payment settled which charge. Many-to-many, because part-payments exist. |
| `Refund` | Always references the payment it reverses. Never a negative payment. |
| `PaymentMethod` | A tokenized instrument. **No card data is ever stored** — the token belongs to the provider. |
| `DunningAttempt` | A retry of a failed recurring charge, with its outcome. |

## Business Rules

| Code | Rule |
|---|---|
| `payments.amount_positive` | Amounts are positive. Direction is the entity's type, not the sign — a negative payment is a refund that escaped its own rules. |
| `payments.currency_matches_tenant` | One operating currency per gym (approved scope). Cross-currency arithmetic is refused, never coerced. |
| `payments.money_via_money_type` | All arithmetic through `Money` with explicit rounding (AD-42). No naked `decimal`. |
| `payments.allocation_not_exceeding_charge` | Allocations against a charge never exceed it. |
| `payments.refund_not_exceeding_payment` | Total refunds never exceed the payment, across several partial refunds. |
| `payments.settled_payment_immutable` | A settled payment is never edited. A correction is a refund plus a new charge, so the trail shows what happened. |
| `payments.idempotent_by_key` | Every charge and refund is an `IIdempotentCommand` (AD-39, R5). A retried request must not charge twice — the single most expensive defect in this module. |
| `payments.no_card_data_stored` | PAN, CVV, and expiry never enter the database, a log, or an audit row. |
| `payments.tax_from_settings` | VAT rate and rounding policy come from settings, not code — jurisdictions differ and rates change. |
| `payments.refund_requires_reason` | And the reason is audited. |

## Invariants

- A charge's outstanding balance is **derived** from its allocations, never stored.
- Every stored converted amount carries the rate, provider, and as-of date used (AD-43), so "why this
  number?" is answerable years later.
- A payment references either a member or a walk-in, never neither.
- Sum of allocations + refunds is always reconcilable to the payment total.

## Future Rules

- Dunning schedule and when non-payment suspends access.
- Partial refunds against a multi-item POS sale.
- Cash reconciliation and till float (see POS).
- Provider webhooks: the inbox pattern (AD-32) already covers duplicate delivery.
- Payment plans and instalments.

## Dependencies

- **Memberships** (what is charged), **POS** (sales), **Members** (the payer), platform **Money**,
  **exchange rates**, **settings**, **idempotency**, **integrations** (payment provider adapter).
- **Events:** `gymos.payments.charge.raised`, `gymos.payments.payment.settled`, `.failed`,
  `gymos.payments.refund.issued`.

## Open Questions

1. **Which provider first**, and does it support the SCA flows required in the EU (PSD2)? Determines
   whether a charge can complete synchronously at all.
2. Does non-payment block door access automatically, or raise a task for staff? Automatic is cheaper;
   wrong once, it is a member turned away at the door.
3. Cyprus VAT treatment of memberships versus retail goods versus services — **verify with accounting
   and legal counsel** before any tax code is written.
4. Retention period for financial records under Cyprus law, and how it interacts with GDPR erasure.
5. Are cash payments in scope for the MVP, and who reconciles them?

---

## How this document is used

Before the module is implemented: walk the Business Rules against the Golden Rule and cut what does
not reduce work for the gym owner. Then turn the survivors into `IBusinessRule` implementations
(domain, pure) or Application-layer guards (cross-aggregate), with the codes below as their `Code`
values. Answer the Open Questions first — every one of them changes the shape of the entities.
