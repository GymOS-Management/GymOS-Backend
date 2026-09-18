# Monitoring — Business Rules

**Module:** `GymOS.Platform` · schema `platform` · **platform-scoped** (job runs and process metrics
describe this deployment, not any one gym)
**Status:** **implemented — M6.** This document records the rules as built.
Design: [`docs/superpowers/specs/2026-09-18-m6-monitoring-design.md`](../superpowers/specs/2026-09-18-m6-monitoring-design.md).

## Purpose

Two questions an operator or a support agent asks that nothing before M6 could answer: *is the
platform healthy right now* (health, jobs, queue depth, this replica's process) and *did the job that
should have run last night actually run*. M6 ships both from real sources — the job host's own record
of what it did, the outbox's own row count, and the API process's own counters — and states plainly,
in the payload, what it does not yet know (outbox lag, fleet-wide process numbers, device state).

## 1. The host records every run; a skipped tick is not a run

Recording lives in `JobHost.RunOnceAsync`, not in any one job, because the host already owns the run's
lifecycle — it acquires the lock, resolves the job, times it, and logs start/finish/fail. Putting the
record there means every job present and future is covered without a change to the job itself, and a
job that throws inside its own constructor is still recorded as failed, because the host resolves the
job *after* calling `IJobRunRecorder.StartAsync`.

The sequence per tick is **lock → Start → resolve job → `ExecuteAsync` → Succeed**, or on any
exception **Fail** then rethrow to the existing failure log. A tick where another instance holds the
lock is **not** a run and writes no row: the lock loser did nothing, and a row for it would double
every job's history on a two-replica deployment.

**A recorder failure never fails the job.** `StartAsync` and the two finish calls are wrapped in the
host's own try/catch, logged at warning with the job's name and which stage failed, and the job runs
regardless — with no row, or a `Running` row that is never finished. Monitoring must never be the
reason a job's real work does not happen. The recorder runs on its own DI scope, its own `AppDbContext`
and connection, so a job's failed transaction cannot roll back its own failure row, and the job's scope
disposing mid-`Fail` cannot take the recorder with it.

`Instance` is `Environment.MachineName` — the replica name on Container Apps, the container id in the
local compose stack — so two replicas racing a job's lock leave distinguishable rows.

## 2. The error-text rule, and why it makes `monitoring.read` safe for SupportAgent

A failed run's `error` column is exactly `exception.GetType().Name + ": " + exception.Message`,
trimmed to 2,000 characters (`JobRunError.Describe`, `JobRun.MaxErrorLength`) — never the stack trace
and never an inner exception chain walked to its end. A `SqlException` message can name a server, and
this column is read by a console panel behind `monitoring.read`, not by a debugger; the full exception
is already in the structured log under the same job name and timestamp. This trimming is what makes it
safe to grant `monitoring.read` to a role that must not see the platform's revenue or its infrastructure
detail (§8) — the one field on a job run that could carry more than it should is bounded before it is
ever written.

## 3. The stale rule

`JobStateAssembler.IsStale` computes staleness on read, from the schedule and the run history; nothing
stores it. A job with **no history at all** is not stale — it has had no chance yet. A job that has a
history is stale when any of these hold:

1. its newest run is still `Running` and `now > startedAt + period + grace`;
2. it has **never succeeded** — `LastSucceededAtUtc` is null — regardless of how long it has been
   running or how recently it failed; or
3. its newest *succeeded* run is older than `now > lastSucceededAtUtc + period + period + grace`.

`period` is the job's `JobSchedule.Period` (the interval, or one day for a daily schedule); `grace` is
`Monitoring:StaleGrace`, defaulting to **5 minutes**, so a run that is merely slow does not flicker red.
Rule 2 is checked unconditionally once a job has any history — it is not gated on elapsed time — so a
job whose very first run fails is reported stale on the next read, not after waiting out two periods:
"has run and never succeeded" is exactly as bad as "has not run in a long time," and the rule treats
them the same rather than giving a new job's first failure a grace window rule 3 does not intend for it.

## 4. Retention: 30 days, the newest run per job always kept

`RetentionJob`'s fifth and last step, `JobRunMaintainer.PurgeAsync`, deletes `platform.job_runs` rows
older than `Monitoring:JobRunRetentionDays` (default **30**, validated `1..3650`) **except the newest
row per `job_name`**, in one set-based, batched statement (5,000 rows a pass, looping while a batch
comes back full):

```sql
DELETE TOP (5000) r FROM platform.job_runs r
WHERE r.started_at_utc < @cutoff
  AND EXISTS (SELECT 1 FROM platform.job_runs newer
              WHERE newer.job_name = r.job_name AND newer.started_at_utc > r.started_at_utc);
```

A job that ran once in the spring still shows its last outcome in the autumn rather than "never ran."
The predicate is a strict `>`, not `>=`: if a job's newest run shares its `started_at_utc` with another
row of the same job — a tie, which the schema does not forbid — **neither row satisfies "a newer row
exists"**, so both survive past the cutoff. Retention therefore only ever **over-retains** on a tie; it
never deletes the row it means to keep. The sweep is idempotent and non-fatal (a `SqlException` or
`InvalidOperationException` is logged and the next run's sweep tries again), and it records itself like
any other job, so the retention job's own health is visible on the page it maintains.

## 5. Outbox depth only

The overview's `outbox` block reports `totalMessages`, `messagesLast24h`, and
`oldestMessageAgeSeconds` (null only when the table is genuinely empty), plus a fixed `note`:
`"Depth only. Delivery lag and dead letters are reported once the outbox processor exists."`
`OutboxSaveChangesInterceptor` writes `outbox.messages` in the same transaction as the state change it
captures, so depth is real and measurable; but nothing subscribes to a message and no
`outbox.deliveries` row exists, so a "lag" computed today would be the age of the oldest message ever
written, growing forever, and would read as a stuck queue when it is really an unbuilt consumer. The
note exists so a console rendering depth without lag does not need a wiki page to explain the gap.

**A freshly seeded platform is never literally empty.** Seeding the SuperAdmin raises `UserCreated`,
which the outbox interceptor captures in the same transaction, so a booted host always has at least one
row. `oldestMessageAgeSeconds` on a database with no other activity is therefore a real, non-negative
age — never `null` — and the integration suite asserts exactly that rather than asserting `null` for an
"empty platform," because there is no host state in which the outbox table is truly empty.

## 6. Process metrics are one replica's view

`process` is the answering replica's own `cpuPercent` (process CPU time delta over the sampler's
15-second interval, divided by core count), `workingSetMb`, `uptimeSeconds`, `instance`
(`Environment.MachineName`), and `sampledAtUtc`. `ProcessMetricsSampler` is a singleton `BackgroundService`
in the host project that samples on a `PeriodicTimer` and publishes the latest reading; the endpoint
never blocks on a measurement, and the first reading after start-up reports `cpuPercent: null` because a
CPU delta needs two samples. With UAT's replica floor at zero this is the whole platform; with several
replicas it is whichever one answered, and `instance` says which. Fleet-wide numbers are App Insights',
and the payload does not pretend to be one.

## 7. Endpoints

| Method | Route | Permission | Error codes |
|---|---|---|---|
| GET | `/api/v1/monitoring/overview` | `monitoring.read` | — |
| GET | `/api/v1/monitoring/jobs` | `monitoring.read` | — |
| GET | `/api/v1/monitoring/jobs/{name}/runs` | `monitoring.read` | `monitoring.job_not_found` (404), `monitoring.invalid_status` (400), `monitoring.invalid_range` (400) |

`/jobs/{name}/runs` takes `from`, `to` (optional, inclusive on `started_at_utc`), `status` (optional,
one of `Running`, `Succeeded`, `Failed`, **parsed case-insensitively by name** — `Enum.TryParse` with
`ignoreCase: true`), and `limit` (clamped to `1..200`, default `50`). `from > to` and an unparseable or
undefined `status` fail before any query runs. `name` is accepted when it names a currently registered
job **or** has any run history at all, so a renamed or removed job's history stays reachable until
retention removes it; otherwise the response is `404 monitoring.job_not_found`. `hasMore` is derived
from fetching `limit + 1` rows rather than a second `COUNT` query — a client pages by moving `to` to the
oldest `startedAtUtc` it received.

## 8. Permissions

`monitoring.read` is a new `PlatformPermissions` constant: `"View platform health, job runs and queue
depth"`. It is granted to **SuperAdmin** (via `GrantsAllPermissions`), **PlatformAdmin**, **SupportAgent**,
and **ReadOnly** — and deliberately **not** to BillingAdmin. This is the opposite shape from M5's
`analytics.read`, which is withheld from SupportAgent because MRR and churn are commercially sensitive.
Nothing in these payloads is: a support agent answering "why has this gym not renewed?" needs to see
that `subscription-renewal` last succeeded an hour ago, and §2's error trimming is what makes that grant
safe rather than a liability. `/dashboard/health` is unchanged and keeps `analytics.read` — it is the
dashboard's own panel, not this module's.

## 9. Deferred, deliberately

| Deferred | Waits on |
|---|---|
| `GET /monitoring/devices`, device registry, heartbeats, `DeviceOfflineJob` | The Gateway module — nothing exists to send a heartbeat yet |
| `GET /monitoring/logs`, any in-app log reading | App Insights (AD-11: deep observability stays there); the console links out |
| `system_metric_snapshots`, a metrics-history collector, CPU/memory history | App Insights; the overview's live block is the in-app view AD-11 asks for |
| Outbox lag, retry counts, dead-letter listing, replay | The outbox-processor increment — there is no delivery row to compute lag from |
| The `JobRunRetentionDays` window as a `platform_settings` row | M7's settings module |
| Running, pausing, or rescheduling a job from the API | No scenario has asked for it; an additive endpoint if one does |
| Alerting, thresholds, notifications on a failed or stale run | AD-44 (notifications surface) — a `stale` flag in a payload is as far as M6 goes |
| The console pages that read any of this | A GymOS-Platform increment; M6 is the API they will read |

## 10. Known limitations, recorded rather than hidden

**A process killed mid-run leaves a `Running` row that is never rewritten.** Nothing about the row
itself changes when the process that started it dies — there is no heartbeat and no lease to expire.
The read side reports it as **stale** once `now` passes `startedAt + period + grace` (§3), which is
correct and is the only signal a reader gets; the row's `status` column keeps saying `Running` forever,
because no later process has any way to know the run it half-owns was ever abandoned rather than simply
slow.

**`GetJobHistoryHeadsAsync` issues two grouped queries plus one per job with history.** The newest
`started_at_utc` per job and the newest *succeeded* `started_at_utc` per job are each one grouped query
over the whole table; then, for every job that has at least one run, a third query loads that job's
single newest row (EF Core has no portable window-function projection here, so the alternative is a
raw-SQL `ROW_NUMBER()`). At five registered jobs this is at most eleven queries against an indexed
column and is not worth avoiding; at fifty it is worth revisiting, most simply as a single query with a
window function.

**The process block is one replica's view, not the fleet's.** §6 states this in the payload's own
shape; it is repeated here because a console built against `/monitoring/overview` without reading the
spec could otherwise present a lucky sample as the whole platform's CPU or memory.

**`status` on `/jobs/{name}/runs` also accepts a defined numeric string.** `Enum.TryParse<JobRunStatus>`
with `ignoreCase: true` parses `"2"` to `Succeeded` exactly as it parses `"Succeeded"`, because the
overload does not distinguish a name from an underlying value when the value is a defined one. This is
not validated against separately; a caller passing `status=2` gets the same result as
`status=Succeeded`, which is harmless but undocumented.
