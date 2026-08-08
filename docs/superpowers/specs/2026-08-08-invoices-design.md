# M3.3 Invoices — Design

**Date:** 2026-08-08 · **Increment:** 2 of 2 for M3.3 (AD-39 idempotency framework shipped first)
**Authority:** `GymOS-Specification/ARCHITECTURE.md` §2.3 (schema, frozen), §3.4 (endpoints, frozen);
`docs/BusinessRules/Subscriptions.md` deferral register; AD-42 (`Money`); AD-6 (confirmation
headers); AD-39 (idempotency — mark-paid is its first production consumer).

## Product decisions taken 2026-08-07 (approved)

1. **Creation**: non-trial subscription assignment issues the first period's invoice; trial
   assignment issues nothing (a trial is a grace on payment) and the invoice appears at trial
   conversion; a plan-change upgrade issues a prorated invoice. The M3.4 renewal job takes over
   subsequent periods. No `POST /invoices` — creation is a domain consequence, matching the
   frozen endpoint table.
2. **Tax**: computation is real (`tax_amount = round(subtotal × tax_rate)`,
   `total = subtotal + tax_amount`) with `tax_rate` fixed at **0%** until accounting counsel
   supplies jurisdiction values. Columns are honest zeros, never NULL.
3. **Plan change policy**: decided by the **sign of the prorated net**, not plan "tier".
   Net ≥ 0 (upgrade) → applied immediately + invoice (credit line for unused old-plan time,
   charge line for the new plan's remainder). Net < 0 (downgrade) → **scheduled at period end**
   (`pending_plan_*` columns on `gym_subscriptions`, the `cancelAtPeriodEnd` pattern) — no
   credit notes, no carried balances, no schema for either.

## 1. Aggregate

`Invoice` root + `InvoiceLine` children, platform schema, **no tenant_id** (the gym is the
customer). **R14/RLS is still not triggered** — invoices are platform-scope; Members remains the
first tenant-scoped entity.

- Fields per §2.3: `gym_id`, `subscription_id`, `invoice_number`, `status`, `issued_at`,
  `due_at`, `paid_at`, `payment_reference`; lines: `description`, `quantity`, `unit_price`,
  `amount`.
- **Amounts are `Money`** (R4 permits nothing else on a class): `Subtotal`, `TaxAmount`,
  `Total` map via `MoneyConventions.HasMoney`, which yields a currency column per amount
  rather than §2.3's single `currency` column. Deviation of the same kind as M3.2's price
  snapshot: the stated property — one currency per invoice — is enforced by an aggregate
  invariant plus a DB CHECK (`subtotal_currency = tax_amount_currency AND … = total_currency`);
  the literal single-column layout cannot coexist with R4.
- `InvoiceStatusTransitions` is the single transition table: `Issued → Paid`, `Issued → Void`.
  **Draft and Overdue are enum values nothing sets yet**: Draft has no producer until manual
  invoicing exists; Overdue belongs to the M3.4 job. Until then "overdue" is **derived**
  (`status == Issued && due_at < now`) in read models only — the M3.2 trial-expiry precedent
  (legible rather than wrong). `Void` and `Paid` are terminal.
- `due_at = issued_at + 14 days` (payment terms), a constant with a name
  (`InvoicePaymentTerms.Days`) — becomes a setting when AD-45 settings land; not configurable
  before then (YAGNI).

## 2. Numbering (frozen: "sequential per year, DB sequence")

`platform.next_invoice_number(p_year int) RETURNS bigint` — raw SQL in the migration (precedent:
`ensure_audit_log_partitions`): creates `platform.invoice_number_seq_<year>` on demand
(idempotent), returns `nextval`. Stored as `invoice_number varchar` formatted `YYYY-NNNNNN`
(`2026-000123`), unique index. Year comes from `issued_at` (UTC), which is `IClock` read at the
edge and passed inward.

**Gap-tolerant by design**: a rolled-back issuing transaction burns a number — standard sequence
semantics. Flagged for counsel (some EU regimes prefer gapless); strict gapless would serialize
every invoice-issuing transaction platform-wide and is not built before counsel asks. Recorded in
the deferral register.

## 3. Issuing paths

One domain-level issuer (`InvoiceIssuer`, Application-layer service — the cross-aggregate
`SuperAdminGuard` precedent) builds an invoice from a subscription + line specs, in the SAME
transaction as the state change that caused it:

| Trigger | Lines | Notes |
|---|---|---|
| Assign (non-trial) | 1: plan name, period, snapshot price | period from M3.2's `current_period_*` |
| Trial conversion | 1: same | conversion stays an explicit op until M3.4 |
| Plan change, net ≥ 0 | 2: credit (negative) for unused old-plan time; charge for new plan remainder | daily proration via `Money.Allocate(daysUsed, daysRemaining)` on the OLD price — the allocate-then-negate rule keeps the spare cent on the same party |
| Plan change, net < 0 | none now | scheduled: `pending_plan_id`, `pending_price_*` (snapshot), `pending_change_at = current_period_end`; **applied by the M3.4 renewal job** — until then it is visible, replaceable state (the M3.2 trial-expiry precedent: legible rather than wrong); `PlanChangeScheduled.v1` raised; a new change request replaces a live scheduled one, and requesting the CURRENT plan cancels it (so `subscriptions.change_to_same_plan` errors only when nothing is pending) |

Proration invariants asserted by tests: `credit + charge == net`, `sum(Allocate parts) == whole`,
negation symmetry, and 0/3-decimal currencies.

## 4. Commands & the idempotency framework's first consumer

- **`MarkInvoicePaidCommand : IIdempotentCommand`** — `OperationKey = "billing.mark-invoice-paid"`,
  `RetentionPeriod = 72h`, `KeyRequired = true`. Records `payment_reference` (free text, the
  operator's bank/reference note) + `paid_at`; raises `InvoicePaid.v1`; when the invoice is the
  subscription's current-period invoice and the subscription is `PastDue`, transitions it back to
  `Active` (M3.2's payment-state op, now driven by its natural cause). DTO carries
  `amount`/`currency` primitives — the Money-free-payload convention test's first production
  subject.
- **`VoidInvoiceCommand`** — guarded by `X-Confirm-Action: <invoice_number>` (AD-6, gym-delete
  precedent). NOT an `IIdempotentCommand`: voiding twice is already a typed business failure via
  the transition table; the confirm header handles the fat-finger case. Void does not resurrect
  or reissue anything — a wrong invoice is voided and a new one issued by a new triggering act.
- Assignment/conversion/plan-change commands from M3.2 gain their issuing behavior; their
  signatures do not change.

## 5. Endpoints (frozen §3.4, verbatim — no additions)

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /api/v1/invoices` | `billing.read` | paged; filters: status, gymId, overdue (derived) |
| `GET /api/v1/invoices/{id}` | `billing.read` | includes lines |
| `POST /api/v1/invoices/{id}/mark-paid` | `billing.manage` | Idempotency-Key REQUIRED (400 without) |
| `POST /api/v1/invoices/{id}/void` | `billing.manage` | X-Confirm-Action must equal the invoice number; missing/mismatched → `invoices.confirmation_mismatch` (typed business failure, the M2 gym-delete pattern) |
| `GET /api/v1/gyms/{id}/invoices` | `billing.read` | the gym-detail tab's source |

**The OpenAPI snapshot changes deliberately** — regenerate via
`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests`, review the diff (R13's
gate is the review).

## 6. Events, audit, business rules

- Events (aggregate-raised, R9-conformant): `InvoiceIssued.v1`, `InvoicePaid.v1`,
  `InvoiceVoided.v1`, `PlanChangeScheduled.v1`.
- `Invoice`, `InvoiceLine` join `PlatformAuditedEntities` (`invoice`, `invoice_line`);
  `payment_reference` is NOT redacted (it is the audit substance, not a secret).
- `docs/BusinessRules/Invoices.md` — stable codes: `invoices.already_paid`,
  `invoices.already_void`, `invoices.not_payable`, `invoices.confirmation_mismatch`,
  `invoices.payment_reference_invalid` (required, ≤200 chars),
  `subscriptions.pending_change_exists` (replaced, not an error — documented),
  `subscriptions.change_to_same_plan`.

## 7. Storage

One migration `AddInvoices`: `platform.invoices`, `platform.invoice_lines`,
`next_invoice_number` function, unique index on `invoice_number`, the currency-consistency
CHECK, and the `gym_subscriptions` `pending_plan_*` columns (addition-to-frozen-table of the
activate-endpoint kind — an addition serving an approved decision, recorded here, no ADR).
Money columns follow `MoneyConventions` (`numeric(14,4)` + `varchar(3)`).

## 8. Testing

- **Domain**: transition matrix (exhaustive), proration properties (above), line-amount =
  quantity × unit_price with explicit rounding, tax at 0% and at a non-zero rate (the mechanism
  is real even though production pins 0), single-currency invariant.
- **Infrastructure/integration**: `next_invoice_number` against real PostgreSQL (year rollover,
  concurrent callers get distinct numbers), storage contract for the Money columns + CHECK.
- **HTTP end-to-end**: assign → invoice appears (gym + list endpoints); trial → no invoice →
  convert → invoice; upgrade → two-line prorated invoice; downgrade → scheduled, no invoice;
  mark-paid full idempotency proof over real HTTP (**the AD-39 deferred tests land here**):
  missing key → 400 `idempotency.key_required`, success, same-key replay → identical body +
  `Idempotency-Replayed: true`, different-body reuse → 422, void after paid → conflict; void
  with wrong confirmation → `invoices.confirmation_mismatch`.
- **Architecture**: existing suites bite on the first real `IIdempotentCommand`
  (Result-shaped, Money-free payload) — no new rules needed.

## Deferral register (updates to docs/BusinessRules/Invoices.md §deferrals)

| Deferred | To | Why |
|---|---|---|
| Credit notes / carried credit balances | needs ADR | no schema; downgrades-at-period-end removes the need for now |
| Overdue automation + dunning | M3.4 | job runner |
| Renewal invoicing | M3.4 | job runner |
| Tax values per jurisdiction | counsel | AD-42's own deferral |
| Gapless numbering | counsel | serializing trade-off not paid speculatively |
| Invoice PDF / delivery | M4+ file storage (AD-44 §) | no file pipeline yet |
| Configurable payment terms | AD-45 settings | constant named today |

## Standing-rules compliance

Frozen architecture implemented as written; the two recorded deviations (per-amount currency
columns; `pending_plan_*` addition) are property-preserving implementation choices with named
precedents. `IClock` only; events in aggregates; `Result` for business failures; one
`SaveChangesAsync` per use case (mark-paid rides the AD-39 explicit transaction automatically);
no new packages. Golden Rule: the gym owner's bill exists without anyone typing it, and a
double-clicked "mark paid" cannot pay twice.
