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

## The plan-change decision tree

Proration only applies when it is well-defined: a **paid, running period, same billing cycle**. The
handler settles the cheap questions first, in this order:

| Case | Behavior | Why |
|---|---|---|
| **Same plan, same terms** | Succeeds as a **no-op** (cancelling a pending change if one is scheduled) | The route is a PUT: a retry after a lost response must succeed. "Put me on the terms I already have" leaves the subscription with no pending change either way. |
| **Trialing** | Plan changes now; **no invoice** | Nothing has been paid, so there is nothing to prorate or credit. Conversion bills the new plan's full first period — trials bill at conversion, never before. |
| **Period already ended** | Plan changes now; **no invoice** | No remainder exists to price, and scheduling would date the change in the past where no job applies it. An admin resolving a lapsed account needs "success" to be true. |
| **PastDue** | Change is **scheduled** for the period end | The period is not settled: crediting "unused time" the gym never paid for would be wrong, and stacking a charge on an unpaid invoice would be worse. |
| **Different billing cycle** | Change is **scheduled** for the period end | Cross-cycle proration is ill-defined — the two prices buy different lengths of time. At the boundary no proration arises. |
| Otherwise, the **net-sign policy**: | | |
| **Net positive** (upgrade) | Plan changes now; a two-line proration invoice is issued | Money owed to the platform is billed when the value is delivered. |
| **Net zero** | Plan changes now; **no invoice** | There is nothing to bill, and a €0.00 invoice is noise an accountant has to explain. |
| **Net negative** (downgrade) | Change is **scheduled** for the period end (`pending_*` columns, visible as `pendingChange`) | The gym paid to the period end, and there is no credit-note document to carry a mid-period refund. Never issued as a negative invoice. |

A scheduled change is **visible, replaceable state**: requesting a third plan replaces it,
re-requesting the current plan at the current terms cancels it, and a change **applied** now clears
one automatically — a change applied today always supersedes a change scheduled for later, or the
renewal job would revert an upgrade the gym just paid for.

## Numbering

`YYYY-NNNNNN`, sequential within each year, drawn from a per-year SQL Server sequence
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
- **A past-due subscription recovers when a payment *settles the current period*** — the paid invoice
  belongs to the period **and** no other open invoice of the period remains, checked and flipped in
  the same transaction. Paying an *old* invoice changes nothing (past due is about the current period,
  not the archive), and paying a small proration invoice while the period's main invoice is still open
  does not open the gate either.
- **Void requires confirmation** — the `X-Confirm-Action` header carrying the invoice number (the M2
  deletion pattern). Voided is a state, not a deletion: the row stays, because "this was withdrawn" is
  exactly what the record must be able to show. A paid invoice cannot be voided.
- **Overdue is both derived and durable (M3.4).** Displays still derive "overdue right now" from
  the due date — an Issued invoice past due reads as overdue even before the sweep's next pass, so
  the night the job fails the lists do not lie. The hourly `InvoiceOverdueJob` additionally writes
  the durable `Overdue` status (guarded: the aggregate refuses the write when not actually past
  due) and raises `gymos.platform.invoice.became_overdue` — the hook dunning email hangs on later.
- **A current period going unpaid takes the subscription PastDue automatically**, in the same
  per-invoice transaction — and settling everything open in the period flips it back (the M3.3
  rule), so the whole loop runs with no operator in it. An *old* invoice crossing its due date
  marks only the invoice. Service is never withheld by any of this.

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

(Re-requesting the current plan at the current terms is deliberately **not** an error: the assign
route is a PUT, and an identical retry after a lost response must succeed as a no-op.)

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
- Transitions: `Issued → Paid | Void | Overdue` and `Overdue → Paid | Void`, held by an
  exhaustively tested 5×5 table. An overdue invoice is **open** — late is not settled, so payment
  and voiding accept it exactly like an issued one. `Draft` remains producer-less until manual
  invoicing exists.

## Deferred, deliberately

| Topic | Where it went |
|---|---|
| Credit notes (mid-period refunds) | Needs its own ADR — a credit note is a document with its own number |
| ~~Overdue status writes + auto-PastDue~~ | **Built — M3.4** (`InvoiceOverdueJob`, hourly) |
| ~~Renewal invoices~~ | **Built — M3.4** (`RenewalJob`, through the same `InvoiceIssuer`) |
| Dunning email on `became_overdue` | Needs the notification surface (AD-44 era); the event already fires |
| Tax values per jurisdiction | Accounting counsel (⚠) |
| Gapless numbering requirement | Accounting counsel (⚠) |
| Invoice PDF | AD-44 file pipeline |
| Configurable payment terms (today: 14 days, `InvoicePaymentTerms.Days`) | AD-45 settings |
