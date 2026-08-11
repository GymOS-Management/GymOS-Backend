# M5 — Dashboard & Analytics — Design

**Status: approved 2026-08-11.** Five decisions taken with the owner: M5 ships **only the stat
columns that have a source**; MRR is reported **per currency** with no conversion; MRR counts
**Active + PastDue**, not trials; churn keeps **cancellations and expiries apart**; and the new
`analytics.read` permission is **not** granted to SupportAgent.

**Frozen scope** (ARCHITECTURE.md §2.4 `daily_platform_stats`, §3.2 dashboard, §3.8 analytics,
AD-8, roadmap item 6): a read model maintained by a background job, a dashboard summary, and
analytics series served from that read model.

## 1. What M5 delivers, and what it defers

The spec's `daily_platform_stats` has eleven columns. **Five of them have nothing behind them**, and
`gym_stats` — members, staff, trainers, storage, visits — is *entirely* tenant counts from a domain
that does not exist. Building a column with no producer means guessing the shape of data that has
not been designed yet, and shipping a dashboard that reports `0` for months, which reads as a broken
system rather than an unbuilt one.

So M5 ships what is real, and the rest arrives with its source. This is the `plan_features` precedent
(deferred M3.1 → M4 *because the catalogue it pointed at did not exist*), and later columns are
additive migrations.

| Delivered now | Deferred, and to what |
|---|---|
| `total_gyms`, `active_gyms`, `suspended_gyms`, `provisioning_gyms` | — |
| `new_gyms`, `canceled_gyms`, `expired_gyms` | — |
| MRR, per currency | AD-43 conversion, if a second currency ever needs summing |
| `/dashboard/summary`, `/health`, `/activity` | — |
| `/analytics/revenue`, `/gyms-growth`, `/churn` | — |
| — | `total_members`, `total_staff` → the Members domain |
| — | `storage_used_mb` → AD-44 file storage |
| — | `api_requests` → a request-metering source nobody has built |
| — | `active_devices`, `/analytics/devices` → M6 device registry |
| — | **all of `gym_stats`** → the Members domain; every column is a tenant count |
| — | `/analytics/active-users`, `/api-usage`, `/storage` → as above |

## 2. Live where it must be current, recorded where it must be history

AD-8 exists so that dashboards "never couple to tenant tables" and stay `O(gyms)` rather than
`O(members)`. **There are no tenant tables yet**, and gym and subscription counts are already
`O(gyms)` — so the rationale that makes a read model necessary does not apply to today's numbers.
Taking AD-8 literally anyway would mean a dashboard that reports **yesterday's** gym count, which is
not a performance trade-off but a wrong answer.

Therefore:

- **`GET /dashboard/summary` reads live.** Counts and current MRR are computed from `gyms` and
  `gym_subscriptions` on request. Cheap now; when it stops being cheap, the read model is already
  there to move it to, and the endpoint's contract does not change.
- **`GET /analytics/*` reads only `daily_platform_stats`**, exactly as §3.8 requires. History cannot
  be computed on demand, because the operational tables hold only current state.

This split is the whole architecture of M5, and it is worth stating plainly: **the dashboard answers
"what is true now", the analytics endpoints answer "what was true then", and they read from different
places for that reason.**

## 3. A stat row is written once and never recomputed

`active_gyms` and `suspended_gyms` are **point-in-time facts**. Nothing in the schema records what a
gym's status was last Tuesday — the row holds today's status and the audit trail holds the changes,
which is not something to rebuild a chart from. A "recompute yesterday" would therefore write
**today's** answer under yesterday's date and call it history.

So the job **inserts if absent and skips if present**, and re-running it is safe by construction
rather than by upsert.

**No backfill, and gaps stay gaps.** The first row is the first run. If the host is down for a night,
that day is missing and stays missing — logged, so it is discoverable. Two temptations are refused
deliberately:

- **Partial backfill.** `new_gyms` (from `gyms.created_at_utc`), `canceled_gyms` (from
  `GymSubscription.CanceledAtUtc`) and `expired_gyms` (from `CurrentPeriodEndUtc`, the boundary the
  renewal sweep expires at) *are* reconstructable; the status counts are not. A row half-real and
  half-invented is worse than an absent one, because nothing on it says which half is which.
- **Zero-filling a gap in the API.** A missing day returned as zeros draws the chart to the floor and
  reads as "we lost every gym that night". `/analytics/*` returns the days that exist. A consumer
  that wants a continuous axis can see the gap and decide for itself.

## 4. Persistence

Migration `AddPlatformStats`:

**`platform.daily_platform_stats`** — `stat_date date` (unique), `total_gyms`, `active_gyms`,
`suspended_gyms`, `provisioning_gyms`, `new_gyms`, `canceled_gyms`, `expired_gyms` (all `int`),
`created_at_utc`. Deleted gyms are excluded from every count: they are not tenants any more. (The M4
review's lesson, applied at the point the numbers are defined rather than discovered later — every
count in this module describes one population.)

`provisioning_gyms` is an **addition** to §2.4's column list, not a change to it: `Provisioning` is a
real status in the frozen gym schema, and without it `active + suspended` does not reconcile to
`total`, which makes the row look wrong to anyone who checks it. Additive, and nothing depends on the
list being exactly five.

**`platform.daily_platform_stats_mrr`** — `(stat_date, currency)` unique, holding a `Money` via
`MoneyConventions.HasMoney` (`amount numeric(14,4)` + `currency varchar(3)`). FK to
`daily_platform_stats` on `stat_date`, cascade.

**Deviation record:** §2.4 specifies `mrr numeric(14,2)` as one column on the stats row. Summing
unlike currencies requires a conversion policy that is an accounting decision (AD-43, and *which*
rate — spot at capture, or month-average), and a single column would force one before anyone has
taken it. The per-currency child table honours the intent — "what is the platform earning per
month" — without inventing an exchange rate. Same shape of deviation as M3.2's price-snapshot FK,
and it collapses back to one number the day AD-43 lands.

Neither table is audited and neither carries a concurrency token: they are derived data written by
one job, and the audit trail of a computed row is the job's log. `xmin` would guard a race that
cannot happen — `IJobLock` already serialises the writer.

## 5. MRR, defined precisely

**Monthly recurring revenue = the sum, per currency, of every live-and-paying subscription's price
normalised to one month.**

- **Included:** `Active` and `PastDue`. PastDue is money genuinely owed on a subscription whose
  service is still running — M3.4 was explicit that billing state never withholds service — so
  dropping it would understate the book and turn a temporary card failure into a revenue dip that
  reverses itself.
- **Excluded:** `Trialing`. A trial has never paid; counting it reports money nobody owes and would
  fall every time a trial ended without converting.
- **A scheduled cancellation still counts** until it actually ends. The gym has paid to the period
  end, and `cancelAtPeriodEnd` deliberately leaves the subscription live (M3.2).
- **Normalisation:** the copied `Price` divided by its `BillingCycle`'s month count (1, 3, or 12) via
  `Money.Multiply` with an explicit `RoundingPolicy.HalfEven`. Twelve monthly figures need not
  re-sum to the annual price: MRR is an indicator, not an invoice, and the rounding is stated here
  rather than left to whoever reads the code.

**One calculator, two callers.** The live summary and the nightly job must not each compute MRR:
that is two definitions of the platform's headline number, and the M4 review's whole lesson was that
two implementations of one rule eventually disagree. `MonthlyRecurringRevenue` is a pure domain
calculator over a set of (status, cycle, price) values returning per-currency totals — the shape
`FeatureResolution` established — and both callers use it. It is testable without a database, and the
dashboard and the chart cannot drift apart.

`SubscriptionStatusTransitions` gains a **`Billing`** set beside the existing `Live`. They are
deliberately different sets — `Live` answers "does the one-per-gym rule apply", `Billing` answers
"does this contribute revenue" — and naming the second rather than writing `Live.Except(Trialing)` at
the call site is what stops the two questions being conflated later.

## 6. Churn keeps its causes apart

M3.2 kept `Canceled` (a decision the customer made) and `Expired` (a failure to collect) as distinct
terminal states, on the reasoning that collapsing them makes churn unanalysable. §2.4's single
`churned_gyms` column would undo exactly that, and the distinction cannot be recovered from the table
afterwards.

So the read model stores **`canceled_gyms`** and **`expired_gyms`**, and `/analytics/churn` returns
both plus their total. The first question anyone asks of a churn chart is *why*, and the answer —
product problem or payments problem — is the only thing that makes the number actionable.

## 7. The job

**`PlatformStatsJob`**, on the `JobHost` M3.4 built: `JobSchedule.DailyAtUtc(03:30)`, after
`RetentionJob`'s 03:10. It computes **yesterday**, under the same `IJobLock` (so only one instance
writes) and `IDatabaseClock` (so the day boundary is the database's clock, not any one host's — the
M3.4 reasoning about durable verdicts applies to a row that will be read as history forever).

Its whole body: determine yesterday's date; return if a row exists; count; write; log. Failures are
logged and the schedule advances, exactly like the other jobs — a missed day is a gap, which §3 has
already decided is the honest outcome.

Day boundaries are **UTC**, not per-gym-local. A platform-wide figure has to be summed over one
clock, and gyms in different time zones would otherwise contribute to different days.

## 8. Endpoints and permissions

| Method | Route | Permission | Source |
|---|---|---|---|
| GET | `/api/v1/dashboard/summary` | `analytics.read` | **Live** |
| GET | `/api/v1/dashboard/health` | `analytics.read` | Live health checks |
| GET | `/api/v1/dashboard/activity?limit=` | **`audit.read`** | `audit_logs`, trimmed |
| GET | `/api/v1/analytics/revenue?from=&to=` | `analytics.read` | `daily_platform_stats` |
| GET | `/api/v1/analytics/gyms-growth?from=&to=` | `analytics.read` | `daily_platform_stats` |
| GET | `/api/v1/analytics/churn?from=&to=` | `analytics.read` | `daily_platform_stats` |

**One new permission, `analytics.read`**, granted to SuperAdmin, PlatformAdmin, BillingAdmin and
ReadOnly — and **not to SupportAgent**. MRR and churn are commercially sensitive, and answering "why
is POS off for this gym?" needs neither. This is the same authority reasoning that split
`features.manage` from `billing.manage` in M4.

**`/dashboard/activity` requires `audit.read`, not `analytics.read`.** It is the audit log in a
trimmed shape, and letting analytics access quietly become audit access would be a permission
boundary crossed by convenience.

`granularity` (§3.8's parameter) is **not** implemented: the read model is daily, and a weekly or
monthly rollup is a client-side sum over the days returned until someone has a reason it is not.

**`from` and `to` are both required**, must satisfy `from <= to`, and may span at most **366 days**
(`analytics.range_invalid`, a 400). An optional range would let a default become an unbounded scan of
a table that grows a row a day forever, and a stated ceiling is a refusal the caller can act on
rather than a page that gets slower every year.

**`/dashboard/health`** projects the health-check report the host already produces for
`/health/ready` — per-check name and status, no exception details or connection strings, since this
endpoint is for a console panel rather than a debugger.

## 9. Out of scope, stated

- **Everything in §1's deferred column.** Nothing in M5 pretends those numbers exist.
- **The console UI** — a GymOS-Platform increment. M5 is the API it will read.
- **AD-43 currency conversion**, per §4.
- **`/monitoring/*` (§3.7)** — that is M6, and its device and job-state sources are M6's.

## 10. Verification plan

**Unit.** MRR normalisation across all three cycles and a zero-decimal currency; the `Billing` set
excluding `Trialing` and including `PastDue`; a scheduled cancellation still counting; the day-range
validator.

**Application.** The job's decision through fakes: computes and writes when the day is absent; writes
**nothing** when the row exists; counts exclude deleted gyms; per-currency grouping produces one
entry per currency rather than a silent sum.

**Integration, over real PostgreSQL.** The job writes one row and a second run leaves it byte-identical
(the write-once rule, which is the design's central claim); a gap survives as a gap rather than being
filled; `/analytics/*` returns only existing days and never zero-fills; the endpoints' permission
matrix, including SupportAgent being refused `analytics.read` and granted `/dashboard/activity`;
`/dashboard/summary` reflecting a subscription assigned moments earlier, which is the point of it
being live.

**Live run.** Seed two gyms on plans in one currency, run the job by hand, read `/analytics/revenue`
and see the MRR row; assign a third gym and watch `/dashboard/summary` move while the analytics
series does not — the live-versus-recorded split, demonstrated rather than asserted.
