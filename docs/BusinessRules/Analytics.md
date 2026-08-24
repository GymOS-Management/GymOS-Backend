# Analytics & Dashboard — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (these numbers describe the
platform's own book, not any one gym's)
**Status:** **implemented — M5.** This document records the rules as built.
Design: [`docs/superpowers/specs/2026-08-11-m5-dashboard-analytics-design.md`](../superpowers/specs/2026-08-11-m5-dashboard-analytics-design.md) —
§4 was amended mid-increment by an owner ruling (the population/event split below); that document is
authoritative over this one's paraphrase of it.

## Purpose

Two questions that sound alike and are answered from different places: *what is true right now* (the
console's dashboard) and *what was true on a given day* (a chart someone draws a trend from). M5
ships both, and ships only the numbers that have a real source behind them today.

## 1. Live where it must be current, recorded where it must be history

**`GET /dashboard/summary` reads live** — gym counts and current MRR are computed from `gyms` and
`gym_subscriptions` on every request. **`GET /analytics/*` reads only `daily_platform_stats`** — a
read model written once a day by a background job.

Why the split, when AD-8 says dashboards should never couple to tenant tables: AD-8's reasoning is
about staying `O(gyms)` instead of `O(members)`. There are no tenant tables yet, and gym/subscription
counts are already `O(gyms)` — so the rationale that makes a read model necessary for members does not
apply to today's gym-and-subscription numbers. Reading the read model literally anyway would mean the
dashboard reports **yesterday's** gym count on the day a new gym is created, which is not a
performance trade-off, it is a wrong answer. So:

- The dashboard answers "what is true now" and is cheap to compute today; when it stops being cheap,
  the read model is already there to move it to, and the endpoint's contract does not change.
- The analytics endpoints answer "what was true then", and history cannot be computed on demand — the
  operational tables hold only current state, not what they held last Tuesday.

This is why assigning a gym moves `/dashboard/summary` immediately and never moves an
`/analytics/*` series until the next night's job runs — demonstrated in the live run below, not just
asserted here.

## 2. A stat row is written once and never recomputed

`active_gyms` and `suspended_gyms` are **point-in-time facts**. Nothing in the schema records what a
gym's status was last Tuesday — the row holds today's status, and the audit trail holds the changes,
which is not something to rebuild a chart from. Re-running the compiler for a day that already has a
row would therefore write **today's** answer under yesterday's date and call it history.

So `DailyStatsCompiler.CompileAsync` **inserts if absent and skips if present** — the write-once rule
is a return value (`false` when the day already existed), not a log line describing an assumption:

- `PlatformStatsJob` computes **yesterday** (never today — the day is not over) under `IJobLock` (one
  writer) and `IDatabaseClock` (the database's clock decides *which date* "yesterday" is, not any one
  host's, the same reasoning M3.4 already applies to durable, terminal verdicts).
- Day boundaries are **UTC**. A platform-wide figure has to be summed over one clock, or gyms in
  different time zones would contribute to different days.

**`IDatabaseClock` only decides the boundary date, not every timestamp compared against it.** The
compiler's own comparisons (`Gym.CreatedAtUtc`, `GymSubscription.CanceledAtUtc`, `CurrentPeriodEndUtc`)
are stamped when those rows were written, by whichever host handled that request, using its own
`IClock` — not `IDatabaseClock`. A writing host's clock skewed against the database's can therefore
file an event a few seconds either side of the true boundary and land it under the wrong day, even
though the day the job itself compiles is exact. This is a narrower guarantee than "the clock deciding
the boundary" reads as, and worth stating precisely rather than implying every timestamp in the row is
equally immune to host skew.

**A gap is honest, and a gap stays a gap.** If the host is down for a night, that day is missing and
stays missing — logged, so it is discoverable, but never invented after the fact. Two temptations were
refused on purpose:

- **Partial backfill.** `new_gyms`, `canceled_gyms`, and `expired_gyms` *are* reconstructable from
  timestamps that already exist; the population counts are not, because nothing records what status a
  gym held on a day that has already passed. A row that is half-real and half-invented is worse than an
  absent one, because nothing on the row says which half is which.
- **Zero-filling a gap in the API.** A missing day returned as zeros draws a chart to the floor and
  reads as "every gym vanished that night." `/analytics/*` returns only the days that exist. A caller
  that wants a continuous axis can see the gap in the dates it got back and decide for itself how to
  render it — `AnalyticsApiTests.AGapInTheSeriesIsReturnedAsAGapAndNeverAsZeros` pins exactly this: two
  seeded days with one skipped in between returns two points, not three, and never a zero-valued third.

## 3. Two families of count, and why they read differently

`daily_platform_stats` holds eight numbers, and they answer two different questions on purpose:

| Family | Columns | Answers |
|---|---|---|
| **Population** | `total_gyms`, `active_gyms`, `suspended_gyms`, `provisioning_gyms` | Who is a tenant **right now** |
| **Events** | `new_gyms`, `canceled_gyms`, `expired_gyms` | What **happened** on this day, regardless of the gym's status now |

**Population counts exclude deleted gyms** — a deleted gym is not a tenant any more, so it cannot be
active, suspended, or provisioning.

**Event counts do not.** A gym created and deleted the same day is still one created: omitting it from
`new_gyms` would drop the gym from the population with nothing in either series to explain why it
disappeared. Deleting a gym cancels its live subscription in the same transaction
(`DeleteGymCommandHandler`), and that cancellation counts in `canceled_gyms` for the same reason — a
deletion is the strongest form of churn there is, not an erasure of it.

**This is a mid-increment correction, recorded rather than hidden.** The design's first draft said
"deleted gyms are excluded from every count," and `new_gyms` was originally written to match — counted
over the same status-filtered set as the population columns. A gym created and deleted on the same day
then vanished from both series with nothing to explain why, which a task-3 review caught. The owner
ruled the *design sentence* was too broad, not the code that reads the way `canceled_gyms`/
`expired_gyms` already did: the sentence now names the population family specifically, and `new_gyms`
was moved to count unfiltered, matching its two siblings. Anyone tempted to "fix" the asymmetry back
should read `DailyStatsCompiler.CompileAsync`'s comment first — it is deliberate, and it is pinned by
`ADeletedGymLeavesThePopulationButStillCountsAsChurn`.

`provisioning_gyms` is present even though the frozen spec's column list does not name it:
`Provisioning` is a real `GymStatus`, and without a column for it `active + suspended` would never
reconcile to `total` — which reads as a bug to anyone who checks the arithmetic. Additive to the
list, not a change to it.

## 4. MRR, defined precisely

**Monthly recurring revenue = the sum, per currency, of every live-and-paying subscription's price,
normalised to one month.**

- **Included statuses:** `Active` and `PastDue`. PastDue is money genuinely owed on a subscription
  whose service is still running — M3.4 was explicit that billing state never withholds service — so
  excluding it would understate the book and turn a temporary card failure into a revenue dip that
  reverses itself for no real reason.
- **Excluded status:** `Trialing`. A trial has never paid; counting it would report money nobody owes,
  and MRR would fall every time a trial ended without converting even though nothing was actually lost.
- **A scheduled cancellation still counts** until the subscription actually ends. The gym has paid to
  the period end, and `cancelAtPeriodEnd` deliberately leaves the subscription `Active` and live (M3.2)
  — the same "still counts" reasoning renewal and billing already apply.
- **Normalisation.** The copied `Price` on the subscription (never the plan's current price — M3.2's
  price-snapshot rule) is divided by its `BillingCycle`'s month count (1, 3, or 12,
  `BillingCycle.Months()`) via `Money.Multiply` with an explicit `RoundingPolicy.HalfEven`. Twelve
  monthly figures need not re-sum to the annual price paid — MRR is an indicator, not an invoice, and
  the rounding policy is stated in the calculator rather than left for whoever reads the code to guess.
- **Per currency, never summed across currencies.** Summing unlike currencies needs a conversion
  policy — and *which* rate (spot at capture, or month-average) — that is an accounting decision (AD-43)
  nobody has taken. `MonthlyRecurringRevenue.Calculate` returns one `Money` per currency present in the
  input, ordered by currency code, and never coerces them into one number.

**This is the design's other recorded deviation.** §2.4 of the frozen spec names `mrr numeric(14,2)` as
one column on the stats row; M5 instead ships a `(stats_id, amount_currency)` child table
(`daily_platform_stats_mrr`, FK to the parent on the surrogate `stats_id`, cascade). It honours the intent — "what is the platform
earning per month" — without inventing an exchange rate. Same shape of deviation as M3.2's
price-snapshot FK: the literal column is unworkable against a fact nobody has decided yet, so the
table records the fact honestly instead.

**This does not collapse back to one number the day AD-43 lands, and it is not meant to.** `mrr` is
already shipped as a JSON **array** on both `/dashboard/summary` and `/analytics/revenue` (§7) —
every v1 consumer already handles `n` entries, including `n = 0` for a platform with no paying gyms.
Replacing that array with a scalar once AD-43 supplies a conversion rate would be a breaking change to
a published contract, not a cleanup; the honest plan is that AD-43 adds a converted total *alongside*
the array, which stays. So the per-currency array is a **permanent** part of the v1 shape, not a
placeholder for a future scalar — and that is the better outcome anyway: a client that must handle a
list is safer than one handed a single number that quietly means "EUR-equivalent at an unspecified
rate." Anyone signing off on this deviation is agreeing to a permanent array, not a temporary one.

**One calculator, two callers, by construction.** `MonthlyRecurringRevenue.Calculate` is a pure domain
function over `(status, cycle, price)` triples — no database, fully unit-testable — and it is the
*only* place MRR is computed. `DashboardQueries.GetSummaryAsync` (the live path) and
`DailyStatsCompiler.CompileAsync` (the nightly path) both call it. The M4 review's lesson was that two
implementations of one rule eventually disagree; M5 does not create a second one to disagree with.

`SubscriptionStatusTransitions` gained a **`Billing`** set (`[Active, PastDue]`) beside the existing
`Live` set. They are deliberately different questions — `Live` answers "does the one-per-gym rule
apply", `Billing` answers "does this contribute revenue" — and naming the second, rather than writing
`Live.Except(Trialing)` at every call site, is what stops the two questions from being quietly
conflated later. `Billing` is a subset of `Live` (a non-live subscription cannot bill), which is itself
an asserted invariant, not an assumption.

## 5. Churn keeps its causes apart

M3.2 kept `Canceled` (a decision the customer made) and `Expired` (a failure to collect) as distinct
terminal states, on the reasoning that collapsing them makes churn unanalysable. The frozen spec's
single `churned_gyms` column would undo exactly that, and the distinction cannot be recovered from a
merged number afterwards.

So the read model stores **`canceled_gyms`** and **`expired_gyms`** separately, and
`GET /analytics/churn` returns both plus their total (`canceled`, `expired`, `total`). The first
question anyone asks of a churn chart is *why* — a product problem or a payments problem — and that is
the only thing that makes the number actionable rather than just alarming.

## 6. Permissions: one new permission, deliberately withheld from one role

`analytics.read` is the only new permission this increment adds. Granted to **SuperAdmin** (via
`GrantsAllPermissions: true`), **PlatformAdmin**, **BillingAdmin**, and **ReadOnly**.

**Not granted to SupportAgent.** MRR and churn are commercially sensitive, and a support agent's job —
answering "why is POS off for this gym?", "why does this gym not have CRM?" — needs the resolved
features and the audit trail, never the platform's revenue. This is the same authority reasoning that
split `features.manage` from `billing.manage` in M4: granting one gym an exception is day-to-day
administration, and neither that nor day-to-day support work requires seeing what the platform earns.
`SystemRoles.cs` carries the grant list with this reasoning as an inline comment at the SupportAgent
block, not just in this document.

**`/dashboard/activity` is gated on `audit.read`, not `analytics.read`**, even though it lives under
`/dashboard`. It is the audit log in a trimmed shape, and letting analytics access quietly become audit
access would be a permission boundary crossed by convenience rather than by decision. SupportAgent, who
holds `audit.read` but not `analytics.read`, is refused `/dashboard/summary` and `/analytics/*` and
granted `/dashboard/activity` — asserted directly by the integration suite's permission matrix.

## 7. Endpoints

| Method | Route | Permission | Source | Query params |
|---|---|---|---|---|
| GET | `/api/v1/dashboard/summary` | `analytics.read` | **Live** (`gyms`, `gym_subscriptions`) | — |
| GET | `/api/v1/dashboard/health` | `analytics.read` | Live health-check report | — |
| GET | `/api/v1/dashboard/activity` | **`audit.read`** | `audit_logs`, trimmed | `limit` (default 20, clamped 1–100) |
| GET | `/api/v1/analytics/revenue` | `analytics.read` | `daily_platform_stats` + `daily_platform_stats_mrr` | `from`, `to` (both required `DateOnly`) |
| GET | `/api/v1/analytics/gyms-growth` | `analytics.read` | `daily_platform_stats` | `from`, `to` |
| GET | `/api/v1/analytics/churn` | `analytics.read` | `daily_platform_stats` | `from`, `to` |

Every route carries **two** enforcement layers, not one: the endpoint's own
`.RequireAuthorization(PlatformPermissions.AnalyticsRead)` (or `AuditRead` for `/activity`), and the
matching `[RequiresPermission]` attribute on the CQRS query type, checked again by
`AuthorizationBehavior`. A convention test (`EveryRequestTypeDeclaresItsPermissionOrAnExplicitExemption`)
fails the build if a new query type carries neither — this caught a real missing attribute during M4
and was applied correctly to every M5 query on the first attempt.

**Response shapes:**

```jsonc
// GET /api/v1/analytics/revenue?from=2026-08-01&to=2026-08-31
[ { "date": "2026-08-01", "mrr": [ { "amount": 120.50, "currency": "EUR" } ] } ]

// GET /api/v1/analytics/gyms-growth?from=...&to=...
[ { "date": "2026-08-01", "totalGyms": 10, "activeGyms": 10, "suspendedGyms": 0,
    "provisioningGyms": 0, "newGyms": 0 } ]

// GET /api/v1/analytics/churn?from=...&to=...
[ { "date": "2026-08-01", "canceled": 2, "expired": 3, "total": 5 } ]

// GET /api/v1/dashboard/summary
{ "totalGyms": 3, "activeGyms": 3, "suspendedGyms": 0, "provisioningGyms": 0,
  "liveSubscriptions": 2, "mrr": [ { "amount": 240.00, "currency": "EUR" } ] }
```

**`granularity` (named in ARCHITECTURE.md §3.8) is not implemented.** The read model is daily; a
weekly or monthly rollup is a client-side sum over the days an endpoint already returns, until a real
consumer asks for a server-side one. Adding it speculatively would be a second aggregation with nobody
to verify it against.

## 8. Value rules and the stable error code

`from` and `to` are both **required** `DateOnly` query parameters — a request omitting either fails
ASP.NET Core model binding before any handler runs, since neither has a default. Given both:

| Code | Kind | When |
|---|---|---|
| `analytics.range_invalid` | Validation (400) | `to` is earlier than `from` |
| `analytics.range_invalid` | Validation (400) | The inclusive span (`to.DayNumber − from.DayNumber + 1`) exceeds **366 days** |

Both conditions share one code; a client branches on `code`, not on the message text, which differs
between them. `AnalyticsRange.Validate` runs first in every handler, before `IAnalyticsQueries` is
touched at all — a malformed range never reaches the read model.

**Why a ceiling rather than an optional range with a default.** An optional range would let a caller
who forgets both parameters default into an unbounded scan of a table that grows one row per day
forever, getting slower every year with no error to tell them why. A stated ceiling is a refusal the
caller can act on immediately instead.

A well-formed, in-range request that matches no recorded days returns `200` with an empty array — not
a separate error code, because "nothing recorded yet" is a true and useful answer, not a failure.

## 9. Persistence

Migration `AddPlatformStats`, schema `platform`:

- **`daily_platform_stats`** — `stat_date date` (unique), the seven population/event `int` columns
  above, `created_at_utc`.
- **`daily_platform_stats_mrr`** — `(stats_id, amount_currency)` unique, one `Money` per row via
  `MoneyConventions.HasMoney` (`amount decimal(14,4)` + `currency nvarchar(3)`), FK to
  `daily_platform_stats` on the surrogate `stats_id` (not `stat_date`), cascade. EF cannot build a
  unique index across a complex property's member, so the uniqueness is raw SQL in the migration —
  the same treatment M3.1 gave the `(plan, cycle, currency)` price index.

**Neither table carries a `row_version` concurrency token, and neither is audited.** Both are derived data
written by exactly one job, serialised by `IJobLock` — there is no concurrent writer for a token to
guard against, and the audit trail of a computed row is the job's own log, not a change history nobody
would read. Exempted in `ConcurrencyModelTests.ExemptFromConcurrency` with that reasoning attached.

## 10. Deferred, deliberately

The frozen spec's `daily_platform_stats` names eleven columns; M5 ships eight of them (adding
`provisioning_gyms`, per §3 above) and defers the rest, along with every endpoint whose numbers would
have no real source behind them yet:

| Deferred | Waits on |
|---|---|
| `total_members`, `total_staff` (`gym_stats`) | The Members domain — frozen in design, not yet implemented |
| `storage_used_mb` | AD-44 file storage |
| `api_requests` | A request-metering source nobody has built |
| `active_devices`, `GET /analytics/devices` | The M6 device registry |
| The rest of `gym_stats` (visits, and every other tenant count) | The Members domain — every column here is a tenant count from a domain that does not exist |
| `GET /analytics/active-users`, `/api-usage`, `/storage` | As above — same missing sources |
| AD-43 currency conversion, and one summed `mrr` column | An accounting decision (which rate, and when) nobody has taken |
| `GET /monitoring/*` (ARCHITECTURE.md §3.7) | M6 — its device and job-state sources are M6's, not M5's |
| The console UI that reads any of this | A GymOS-Platform increment; M5 is the API it will read |
| Server-side `granularity` rollups | A real consumer asking for one — see §7 |

The reasoning for shipping only what has a source, rather than a column that reports `0` for months
and reads as a broken system, is the `plan_features` precedent already set in M3.1 → M4: build it when
its data exists, not when its slot in the schema does.

## 11. Known limitations, recorded rather than hidden

Two properties of the churn columns are true of what shipped and are not obvious from reading the
endpoint table in §7. Both were found in the whole-branch review; neither blocks merge, and neither is
fixed here.

**An expiry recorded late by `RenewalJob` is lost from `expired_gyms` forever.** `DailyStatsCompiler`
counts a subscription as an expiry for day *D* when its status is `Expired` **and**
`CurrentPeriodEndUtc` falls inside *D* — there is no column recording when the expiry was *noticed*,
only the business date it targets. In the ordinary case this is invisible: `RenewalJob` runs every 15
minutes, so a subscription is flipped to `Expired` within minutes of its period ending, long before
`PlatformStatsJob` compiles that day. But `PlatformStatsJob` runs at 03:30 UTC and compiles
*yesterday*, so the gap between a period ending and that day's compile ranges from about **3.5 hours**
(a period ending at yesterday's 23:59:59 UTC) to about **27.5 hours** (a period ending at yesterday's
00:00:00 UTC). If `RenewalJob` lags past that window — a subscription that keeps throwing inside the
sweep, or a deploy window with `Jobs:Enabled=false` — the subscription is still `Active` when its day
is compiled. Because a stats row is written once and never revisited (§2), that day's row is now
permanent: when `RenewalJob` eventually catches up and correctly flips the subscription to `Expired`,
the expiry has no day left to be counted on. The gym silently disappears from the churn series with
nothing in either series to explain why — the same shape of problem §3 already names for `new_gyms`,
but unresolved here because, unlike `CreatedAtUtc`, nothing on `GymSubscription` records *when* the
expiry was recorded, only what it targets.

**The real fix** is an `ExpiredAtUtc` column on `GymSubscription`, set when `RenewalJob` actually flips
the status, so `expired_gyms` can key on when the expiry was *recorded* — exactly matching how
`canceled_gyms` already keys on `CanceledAtUtc` rather than on the period the cancellation applies to.
That is a schema change to an M3.2 aggregate, and belongs in its own increment, not a fix folded into
this one.

**The two churn columns key on different clocks, and `/analytics/churn` presents them as symmetric.**
`canceled_gyms` counts `CanceledAtUtc` — the real moment cancellation ran. `expired_gyms` counts
`CurrentPeriodEndUtc` — a business date a period targets, not a moment anything ran (and, per the
limitation above, not even reliably that). A subscription canceled at 23:58 UTC and one whose period
ends at 00:02 UTC the same calendar day are three minutes apart in real time but land on different
`stat_date` rows. `GET /analytics/churn` returns `canceled`, `expired`, and their `total` side by side
as if both were counted the same way; they are not, and a caller correlating the two day-by-day, or
summing `total` across a short window, should know the alignment between them is approximate rather
than exact.
