# Invoices — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (the gym is the customer here,
not the tenant whose data this is)
**Status:** **implemented — M3.3.** This document records the rules as built.

## Purpose

What a gym owes for a period of its subscription, and whether it has paid. An invoice is a demand for
money and, once issued, a historical document: nothing edits one, and nothing deletes one. The two
things that can happen to an issued invoice are recording a payment and voiding it.

## Entities

| Entity | Notes |
|---|---|
| `Invoice` | Aggregate root. Platform-scoped, audited, **never deleted** — no `DELETE` route exists. Holds `Subtotal`/`TaxAmount`/`Total` as `Money`. |
| `InvoiceLine` | Part of the invoice aggregate, created only through it, never edited. Negative amounts are legal (credit lines); a negative invoice *total* is not. |

## The three creation paths — and why there is no POST /invoices

Invoices are created as a **consequence**, never on request, each inside the same transaction as the
subscription change that caused it — so a gym can never be charged for something that did not happen:

1. **Assignment without a trial** — one line for the first period at the agreed price.
2. **Trial conversion** — the same first-period invoice, issued at the moment the gym starts paying.
3. **A plan change that costs money** — two lines: a credit for the unused time on the old plan, a
   charge for the remainder of the period on the new one (days-based, via `Money.Allocate`, so the
   split conserves every cent).

## The net-sign policy

A plan change's proration nets in one of three directions, and each has exactly one behavior:

| Net | Behavior | Why |
|---|---|---|
| **Positive** (upgrade) | Plan changes now; a two-line proration invoice is issued | Money owed to the platform is billed when the value is delivered. |
| **Zero** | Plan changes now; **no invoice** | There is nothing to bill, and a €0.00 invoice is noise an accountant has to explain. |
| **Negative** (downgrade) | Plan does **not** change; the change is **scheduled** for the period end (`pending_*` columns, visible as `pendingChange`) | The gym paid to the period end, and there is no credit-note document to carry a mid-period refund. Never issued as a negative invoice. |

A scheduled change is **visible, replaceable state**: requesting a third plan replaces it, and
re-requesting the current plan at the current terms cancels it. Re-requesting the current terms with
nothing pending is refused (`subscriptions.change_to_same_plan`) rather than treated as a no-op —
"nothing changed" and "your request did something" must not look identical. A change of *terms* on the
same plan (monthly → yearly) is a real change and takes the proration path, which is what keeps a gym
on a withdrawn plan adjustable.

## Numbering

`YYYY-NNNNNN`, sequential within each year, drawn from a per-year PostgreSQL sequence
(`platform.next_invoice_number`) created on first use. Unique by index, **immutable** — the number
leaves the system the moment it is issued.

**Gap-tolerant, deliberately** (⚠ flagged for accounting counsel): the number is drawn inside the
issuing transaction, and a rollback after the draw burns it. Sequences do not give numbers back —
making them do so would serialize every issuer behind a lock. If counsel requires gapless numbering,
the mechanism changes; the interface (`IInvoiceNumberGenerator`) does not.

## Payment and voiding

- **Mark-paid is the platform's first production `IIdempotentCommand`** (AD-39). It moves money, so
  the `Idempotency-Key` header is **required**: a retried request replays the stored outcome — byte
  for byte, flagged `Idempotency-Replayed: true` — instead of recording a second payment. A reused key
  with a different body is a 422 (`idempotency.key_reuse_mismatch`).
- **The payment reference is required** (`invoices.payment_reference_invalid`, trimmed, ≤ 200 chars):
  "paid" without a reference is an assertion nobody can check months later.
- **Paying the current period's invoice recovers a past-due subscription** in the same transaction —
  that is what "the payment arrived" means. Paying an *old* invoice does not: the subscription is past
  due about the current period, not the archive.
- **Void requires confirmation** — the `X-Confirm-Action` header carrying the invoice number (the M2
  deletion pattern). Voided is a state, not a deletion: the row stays, because "this was withdrawn" is
  exactly what the record must be able to show. A paid invoice cannot be voided.
- **Overdue is derived, never stored**: `Issued && now ≥ dueAtUtc`, answered from the clock at read
  time. A stored flag needs a job to keep it true, and the night that job failed it would lie. The
  `Overdue` status value exists for the M3.4 job to write when there is something to write it *for*.

## Value rules and stable codes

| Code | Kind | When |
|---|---|---|
| `invoices.payment_reference_invalid` | Validation | Reference missing, blank, or over 200 chars |
| `invoices.status_invalid` | Validation | Unknown `status` filter value — a 400, never a silently empty page |
| `invoices.not_found` | NotFound | No such invoice |
| `invoices.already_paid` | Conflict | Paying or voiding an invoice that is already paid |
| `invoices.already_void` | Conflict | Voiding twice |
| `invoices.not_payable` | Conflict | Paying anything not in `Issued` |
| `invoices.confirmation_mismatch` | Conflict | Void without the invoice number in `X-Confirm-Action` |
| `subscriptions.change_to_same_plan` | Conflict | Re-requesting the current plan and terms with nothing pending |

## Invariants

- An invoice has ≥ 1 line, all lines in **one currency**, held twice: by the aggregate (typed error)
  and by a CHECK constraint (`ck_invoices_single_currency`) that holds under anything that is not the
  aggregate. (Deviation record: three `Money` column pairs replace §2.3's single currency column
  because R4 forbids naked money decimals; the CHECK restores the property the single column gave for
  free.)
- **Line quantity is always 1 in M3.3**, enforced with a named error: whoever first needs quantity > 1
  must add the multiply-with-rounding path deliberately rather than inherit a silent
  `Amount = UnitPrice` bug.
- Tax is computed (`Subtotal × TaxRate`, half-even, rounded to the **currency's** minor units — KWD has
  three, JPY none), stored on the invoice as a fact about the document, and **zero until counsel
  supplies jurisdiction values** (⚠ flagged). Switching it on is configuration, not schema.
- A negative total is unreachable: the net-sign policy routes net credits to scheduling. Reaching the
  guard is a caller defect, not a business case.
- Transitions: `Issued → Paid` and `Issued → Void` only, held by an exhaustively tested 5×5 table.
  `Draft` and `Overdue` are currently producer-less (doc comments name their future producers).

## Deferred, deliberately

| Topic | Where it went |
|---|---|
| Credit notes (mid-period refunds) | Needs its own ADR — a credit note is a document with its own number |
| Overdue automation (dunning, `Overdue` status writes) | M3.4 job |
| Renewal invoices | M3.4 job, through the same `InvoiceIssuer` |
| Tax values per jurisdiction | Accounting counsel (⚠) |
| Gapless numbering requirement | Accounting counsel (⚠) |
| Invoice PDF | AD-44 file pipeline |
| Configurable payment terms (today: 14 days, `InvoicePaymentTerms.Days`) | AD-45 settings |
