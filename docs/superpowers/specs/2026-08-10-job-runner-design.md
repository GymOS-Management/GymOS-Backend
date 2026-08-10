# M3.4 — Job Runner, Renewal & Overdue Jobs — Design

**Status: approved 2026-08-10.** Three product decisions taken with the owner: hand-rolled
schedules (no cron dependency), overdue period invoices auto-flip the subscription to PastDue, and
an expired trial auto-converts and bills. Everything else follows the frozen architecture
(ARCHITECTURE.md folder plan: `Infrastructure/BackgroundJobs`, `IHostedService` + schedule;
`InvoiceOverdueJob` and `GymPurgeJob` are named there — `GymPurgeJob` stays out of scope until the
GDPR purge increment).

## 1. The job runner (BuildingBlocks — module-agnostic)

**`IScheduledJob`** (BuildingBlocks.Application.Jobs):

```csharp
public interface IScheduledJob
{
    /// <summary>Stable name — the advisory-lock key and the log correlation value.</summary>
    string Name { get; }

    JobSchedule Schedule { get; }

    Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
```

**`JobSchedule`** — a closed set, not cron: `JobSchedule.Every(TimeSpan)` and
`JobSchedule.DailyAtUtc(TimeOnly)`. Both answer `NextAfter(DateTimeOffset)`; the M3.4 jobs need
nothing richer, and a cron library is a dependency-policy amendment nobody has to make today. The
spec's "IHostedService + cron" is satisfied in shape — a schedule the host evaluates — with the
expression language deliberately minimal.

**`JobHost`** (BuildingBlocks.Infrastructure.Jobs) — one `BackgroundService` looping per registered
job:

- Computes the next due time from `IClock`, delays, then runs the job in a fresh DI scope.
- **Advisory lock per run** — `pg_try_advisory_lock(hashtext('gymos:job:' || name))` on a dedicated
  connection: two API replicas (or an API and a future worker) never run the same job concurrently;
  the loser skips the tick and logs at debug. Same database-centred coordination as the outbox's
  SKIP LOCKED (AD-13) — no new infrastructure.
- **A failing run never kills the loop**: exceptions are caught, logged as errors with the job
  name, and the schedule advances. A job that must not lose work derives it from state, not from
  the tick (see §3) — every M3.4 job is a sweep over rows whose columns say "due".
- Jobs run **in the API host** today. The compose file already anticipates a separate `worker`
  entrypoint sharing the image; because jobs are DI-registered `IScheduledJob`s, that split is a
  host decision later, not a rewrite.
- `Jobs:Enabled` (default true) turns the host off — needed for integration tests that drive time
  by hand, and for a future many-replica deployment that wants jobs on one profile only.

**Observability**: source-generated `LoggerMessage` start/finish/fail lines with duration; the
M6 monitoring page's `GET /jobs` reads whatever bookkeeping it needs when it lands — no
`job_runs` table now, because nothing reads it yet and the sweeps are state-driven.

## 2. RetentionJob — the two startup maintainers fold in (standing STATUS.md instruction)

Daily at 03:10 UTC. Calls exactly what the two startup stand-ins call today:
`platform.ensure_audit_log_partitions` (AuditLogPartitionMaintainer) and the expired
idempotency-record purge (IdempotencyRecordMaintainer). The startup maintainers stay — first boot
on an empty database still needs partitions before the first request — but the *recurring* halves
now have a real schedule instead of "whenever the process restarts".

## 3. RenewalJob — every 15 minutes

One sweep, four state-derived questions, each subscription in its own transaction (a failure
isolates to one gym and the next tick retries it — every step is idempotent because the
conditions are read from the row, not from a work queue):

1. **Expired trials** (`Trialing`, `TrialEndsAtUtc <= now`): **convert and bill** — the aggregate's
   `ConvertTrial()` plus `InvoiceIssuer.IssueForPeriodAsync`, exactly what the explicit endpoint
   does. "A trial is a grace on payment, not a pause on the calendar."
2. **Scheduled cancellations** (`CancelAtPeriodEnd`, `CurrentPeriodEndUtc <= now`): finalize —
   `Canceled`, the period the gym paid for having fully run.
3. **Period end with a pending change** (`PendingChange.ChangeAtUtc <= now`): apply it —
   `ChangePlan` with the stored terms (plan loaded by id; recall M3.1 makes plans undeletable),
   then renew into the new terms and issue the renewal invoice.
4. **Plain renewal** (`Active`/`PastDue`, `AutoRenew`, `CurrentPeriodEndUtc <= now`): advance the
   period by the billing cycle and issue the renewal invoice at the **subscription's copied
   price** (the snapshot rule: renewals honor the price at assignment). `AutoRenew == false` with
   no cancellation scheduled → `Expired` (time ran out; distinct from `Canceled` by decision, per
   the churn rule).

New domain operations on `GymSubscription`, each transition-table-guarded: `Renew(now)` (advances
`CurrentPeriodStartUtc/EndUtc` by the cycle), `FinalizeScheduledCancellation(now)`, `Expire(now)`,
and `ApplyPendingChange` composed from existing `ChangePlan` + `Renew`. A PastDue subscription
**renews too** — service is never withheld by billing state, so the period advances and the new
period's invoice is issued while the old one stays open; recovery still requires settling
*everything* open in the current period (M3.3's rule, unchanged).

## 4. InvoiceOverdueJob — hourly

- `Issued` invoices with `DueAtUtc <= now` → **`Overdue`** — the status finally gets its producer.
  Transition table gains `Issued → Overdue` and `Overdue → Paid | Void` (mark-paid and void accept
  Overdue exactly like Issued; `IsOpen` already includes it, and the M3.3 read models derived
  overdue from the same predicate, so API consumers see no behavioral change in lists).
- **Auto-PastDue** (approved): when a subscription's current-period invoice goes Overdue and the
  subscription is `Active`, it is marked `PastDue` in the same per-subscription transaction.
  Mark-paid's settled-period rule (M3.3) already flips it back. Service is not withheld — PastDue
  remains bookkeeping that suspension, a human decision, may follow.
- Events: `gymos.platform.invoice.became_overdue` raised by the aggregate's new `MarkOverdue(now)`
  (guarded: only when actually past due at `now`).

## 5. Out of scope, stated

- `GymStatsRefreshJob`, `DailyStatsJob`, `DeviceOfflineJob` — M5/M6, they only get the runner.
- `GymPurgeJob` (GDPR hard delete) — needs the retention-window policy from counsel.
- Dunning email — needs the AD-44/notifications surface; `became_overdue` is the hook.
- Outbox delivery processor — still "first event handler need" (STATUS known open items).
- A `job_runs` table / `GET /jobs` — M6 monitoring reads it into existence.

## 6. Verification plan

Unit: schedule arithmetic (Every/DailyAtUtc next-run math, DST-free because UTC-only); domain
transitions (`Renew`, `Expire`, `FinalizeScheduledCancellation`, `MarkOverdue`, extended
transition tables). Application: renewal sweep decision tests through the real dispatcher-less
service with fakes (one per §3 case, plus idempotency: running the sweep twice changes nothing).
Integration: JobHost advisory lock (two hosts, one runs), RetentionJob calls both maintainers'
SQL, renewal end-to-end over real PostgreSQL (assign → time-travel the period end via seeded
dates → sweep → renewed period + renewal invoice), overdue end-to-end (issued past due → Overdue +
PastDue → pay → Active). Live run: compose stack, short-interval schedule, watch a renewal happen.
