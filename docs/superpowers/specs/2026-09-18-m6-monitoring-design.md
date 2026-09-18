# M6 — Monitoring — Design

**Status: approved 2026-09-18.** Three decisions taken with the owner: the overview shows **health
checks, job state, outbox depth and the API process's live metrics**, with no metric-history table;
job run history is kept **30 days**, with the newest run of every job always retained; and run
recording lives **in the job host**, behind an abstraction, so every job present and future is covered
without touching one.

**Frozen scope** (ARCHITECTURE.md §3.7 monitoring, §2.6 `system_metric_snapshots`, AD-11, roadmap
item 7, folder plan `Infrastructure/Monitoring`), narrowed by the 2026-09-03 decision recorded in
STATUS.md: **M6 ships monitoring without devices.** The device registry, heartbeat endpoint and
`DeviceOfflineJob` are the Gateway module's, because nothing exists to send a heartbeat and a registry
with no producer reports empty for months — the same trap M5 avoided by deferring `active_devices`.

## 1. What M6 delivers, and what it defers

The frozen §3.7 names four endpoints. Two have sources today, one has a partial source, and one has
none. M6 ships what is real, exactly as M5 did with `daily_platform_stats`, and the rest arrives with
its producer as an additive change.

| Delivered now | Deferred, and to what |
|---|---|
| `GET /monitoring/overview` — health, jobs, outbox depth, process | CPU/memory *history* → App Insights (AD-11); `system_metric_snapshots` waits for a reason the live number is not enough |
| `GET /monitoring/jobs`, `GET /monitoring/jobs/{name}/runs` | — |
| **`platform.job_runs`** — the job host records every run | — |
| Outbox **depth** (messages captured, oldest, last 24 h) | Outbox **lag**, retries, dead letters → the outbox-processor increment; there is no processor and no delivery row yet, so "unprocessed" is every message |
| `RetentionJob` sweeps job runs on a 30-day window | The window becomes a `platform_settings` row in M7 |
| — | `GET /monitoring/devices` → the Gateway module's device registry |
| — | `GET /monitoring/logs` → not built in-app at all: structured logs live in App Insights (AD-11: "deep observability stays in App Insights"); the console links out |
| — | "Cloud status" in the overview → no source; nothing in the process can answer for Azure |

**Why outbox lag has no number today.** `OutboxSaveChangesInterceptor` writes `outbox.messages` in
the state change's transaction, so capture is real and measurable. But no handler subscribes, so no
`outbox.deliveries` row exists and nothing marks a message processed. A "lag" computed now would be
the age of the oldest message ever written, growing forever, and would read as a stuck queue when it
is an unbuilt consumer. Depth is the honest figure; lag arrives with the processor, whose spec will
extend the overview's outbox block additively.

## 2. The job host records every run; the jobs do not

The M3.4 spec left this open on purpose: *"no `job_runs` table now, because nothing reads it yet …
M6 monitoring reads it into existence."* Now something reads it.

**Recording is the host's job, not each job's.** `JobHost.RunOnceAsync` already owns the run's
lifecycle — acquire the lock, resolve the job, time it, log start/finish/fail — and the record is that
lifecycle written down. Putting it in the host means: a job that throws in its constructor is still
recorded as failed (the loop guards that case today and the record must not lose it); a future job is
covered the moment it is registered; and no job's transaction can roll back its own failure record,
because the recorder runs on its own scope and connection.

```csharp
// BuildingBlocks.Application.Jobs
public interface IJobRunRecorder
{
    /// <summary>Writes the Running row. Returns its id for the matching Finish.</summary>
    Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken ct);

    Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken ct);

    /// <summary><paramref name="error"/> is already trimmed by the host: type and message, no stack.</summary>
    Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken ct);
}
```

The host's sequence per tick becomes: lock → **Start** → resolve job → `ExecuteAsync` → **Succeed**,
or on any exception **Fail** then rethrow to the existing `LogFailed` path. A **skipped tick** (another
instance holds the lock) is **not** a run and is not recorded: the lock loser did nothing, and a row for
it would double every daily job's history on a two-replica deployment.

**The recorder cannot break the job.** A recorder failure — database unreachable at `Start`, say — is
caught in the host, logged at warning with the job name, and the job still runs; the run simply has no
row. Monitoring must never be the reason billing does not renew. Conversely a job's failure is
recorded on the recorder's own connection after the job's scope has already thrown, so the failure
row lands even when the job's transaction did not.

**Instance.** `Environment.MachineName`, which on Container Apps is the replica name. Two replicas
racing the lock leave distinguishable rows; the local compose stack shows the container id.

**Error text.** `exception.GetType().Name + ": " + exception.Message`, truncated to 2,000 characters.
Never the stack trace and never an inner exception chain walked to its end: a `SqlException` message
can name a server, and this table is read by a console panel, not a debugger. The full exception is
already in the structured log with the same job name and timestamp.

**A process that dies mid-run leaves a `Running` row.** That is the truth, and nothing rewrites it. The
read side (§4) reports a run as **stale** when it has been `Running` past the job's next due time plus
a grace period; the row itself keeps saying what happened.

`JobSchedule` gains two members the read side needs and the host can supply: `Period` (the interval,
or 24 hours for a daily schedule) and `Describe()` (`"every 15m"`, `"daily at 03:10 UTC"`). Both are
pure functions of state the record already holds.

## 3. Persistence

**`platform.job_runs`**

| column | type | notes |
|---|---|---|
| `id` | `uniqueidentifier` PK | |
| `job_name` | `nvarchar(100)` not null | `IScheduledJob.Name` — the lock key, the log key, and now the history key |
| `instance` | `nvarchar(100)` not null | replica or container that ran it |
| `status` | `tinyint` not null | `Running = 1`, `Succeeded = 2`, `Failed = 3` |
| `started_at_utc` | `datetimeoffset` not null | the host's clock at the tick's edge (AD-40) |
| `finished_at_utc` | `datetimeoffset` null | null while `Running` |
| `duration_ms` | `bigint` null | `finished − started`, stored so the page never subtracts |
| `error` | `nvarchar(2000)` null | trimmed as in §2; null unless `Failed` |

Index `ix_job_runs_name_started` on `(job_name, started_at_utc DESC)`: it serves the history page,
the newest-run-per-job query, and the retention sweep's "all but the newest" in one shape. **No row
version**: one writer per row, serialised by `IJobLock`, and after `Finish` a row is never touched.
**No foreign key** to a jobs table, because there is none — the registered set lives in code, and a
job renamed or removed keeps its history under its old name until retention takes it.

`JobRun` is a plain entity in `Platform.Domain.Monitoring`, not an aggregate with a transition table:
it has one legal path (`Running` → `Succeeded | Failed`) that only the host walks, and guarding it
with a domain method would be ceremony. The configuration is `JobRunConfiguration` beside
`DailyPlatformStatsConfiguration`, table in the `platform` schema, snake_case like every other table.
One migration, `AddJobRuns`, on the SQL Server baseline.

**Retention.** `RetentionJob` gains a fifth step, `JobRunMaintainer.PurgeAsync`, after the existing
four: delete `job_runs` older than `Monitoring:JobRunRetentionDays` (default **30**, validated
`>= 1` on start) **except the newest row per `job_name`**, so a job that ran once in the spring still
shows its last outcome in the autumn rather than "never ran". One set-based statement:

```sql
DELETE r FROM platform.job_runs r
WHERE r.started_at_utc < @cutoff
  AND EXISTS (SELECT 1 FROM platform.job_runs newer
              WHERE newer.job_name = r.job_name AND newer.started_at_utc > r.started_at_utc);
```

Batched at 5,000 rows a pass so a long-unswept table never takes one lock; idempotent, non-fatal, logging the count.
The retention run records itself like any other run, and the sweep deletes its own predecessors — a
retention job with no visible history would be the one job whose health nobody could see.

Bound as `MonitoringOptions` from the `Monitoring` section. When M7's settings module lands, the
retention window is one of the "retention windows" the frozen §2.7 already names as a settings row,
and this option becomes its fallback; the option's name is chosen so the settings key can be the same.

## 4. The overview

`GET /monitoring/overview` returns one document with four blocks, each independently sourced and each
saying what it does not know. **Everything is live**: nothing here is a read model, because every
number is `O(jobs)` or a single aggregate over an indexed table, and the M5 argument for reading live
where the read model's rationale does not apply holds again.

**`health`** — `IDashboardHealthCheck.CheckAsync`, the projection `/dashboard/health` already returns
(overall status plus `name → status`, no descriptions, no exceptions). Reused as-is through the same
abstraction; the dashboard endpoint stays where it is for the console's summary panel, and the
overview embeds the same shape. Two endpoints, one source, no drift.

**`jobs`** — one entry per **registered** job, from the host's `ScheduledJobRegistration` set, so a job
that has never run appears as `lastRun: null` rather than being absent. Per job: `name`,
`schedule` (`Describe()`), `lastRun` (`startedAtUtc`, `status`, `durationMs`, `instance`, `error`),
`lastSucceededAtUtc`, `nextDueUtc` (`Schedule.NextAfter(now)`), and `stale`. **Stale** is true when
either (a) the newest run is `Running` and `now > startedAt + Period + grace`, or (b) the newest
*succeeded* run is older than `2 × Period + grace` — a daily job that has missed a night, a
15-minute job silent for half an hour. `grace` is `Monitoring:StaleGrace`, default 5 minutes, so a
run that is merely slow does not flicker red. Stale is computed on read from the rows and the
schedule; nothing stores it. The `IScheduledJob` prototypes are resolved the way the host resolves
them at start-up, in a throwaway scope; the registration list is a singleton already.

**`outbox`** — `totalMessages`, `messagesLast24h`, `oldestMessageAgeSeconds` (null when empty), and
a fixed `note` string stating that delivery lag and dead letters are reported once the outbox
processor exists. Three aggregates over `outbox.messages`, the first two on `created_at_utc`, which
is indexed for the processor that will need it. The note is deliberate: a console panel showing depth
without lag invites the question, and the answer belongs in the payload, not in a wiki.

**`process`** — the API process's own `cpuPercent` (process CPU time delta over the sampler's
interval, divided by cores), `workingSetMb`, `uptimeSeconds`, `instance`, `sampledAtUtc`. A singleton
`ProcessMetricsSampler` in the host project takes a reading every 15 seconds off a timer, so the
endpoint returns the last reading and never blocks on a measurement; the first reading after start-up
reports `cpuPercent: null` because a delta needs two samples. **This is one replica's view.** With
min replicas 0 on UAT it is the whole platform; with several replicas it is whichever answered, and
`instance` says which. Fleet-wide numbers are App Insights', and the spec does not pretend otherwise.

Every block that reads the database — jobs, outbox — reads through `IMonitoringQueries` in
`Platform.Application.Monitoring.Abstractions`, implemented in `Platform.Infrastructure.Monitoring`
with the DbContext, the same shape as `IDashboardQueries`. Health and process are host concerns
(`HealthCheckService` and `System.Diagnostics.Process` are host-side types, see
`DashboardHealthCheckAdapter`'s remarks) and are adapted behind `IDashboardHealthCheck` and a new
`IProcessMetrics`, both registered by hand in `Program.cs` for the reason that adapter documents.

## 5. The jobs page

`GET /monitoring/jobs` — the `jobs` block of the overview on its own, for a page that polls job state
without the other three blocks. Same query, same shape.

`GET /monitoring/jobs/{name}/runs?from=&to=&status=&limit=` — run history for one job, **newest
first**. `name` must be a registered job or one with rows (a renamed job's history stays readable
until retention removes it); otherwise **404**. `from`/`to` are optional, inclusive, on
`started_at_utc`; `status` is optional and one of `Running | Succeeded | Failed`; `limit` is clamped
to `1..200` with default 50, and the response carries `hasMore` so a console can page by moving `to`
to the oldest returned `startedAtUtc`. No offset paging: the index is on time, and time is what the
page scrolls.

No `POST /monitoring/jobs/{name}/run`. Running a job by hand from the console is an operational
action with audit and authorisation questions of its own, and no scenario has asked for it. If one
does, it is an additive endpoint and a separate decision.

## 6. Endpoints and permissions

| Method | Route | Permission |
|---|---|---|
| GET | `/api/v1/monitoring/overview` | `monitoring.read` |
| GET | `/api/v1/monitoring/jobs` | `monitoring.read` |
| GET | `/api/v1/monitoring/jobs/{name}/runs` | `monitoring.read` |

**`monitoring.read`** is a new `PlatformPermissions` constant, described "View platform health, job
runs and queue depth". **Granted to SupportAgent** as well as SuperAdmin and the read-only role: a
support agent answering "why has this gym not renewed?" needs to see that `renewal` last succeeded
an hour ago, and nothing in these payloads is commercially sensitive the way MRR is — the M5 reason
for withholding `analytics.read` does not apply. The `error` column is the one field that could
carry more than it should, and §2's trimming rule is what makes granting this permission safe.
`/dashboard/health` keeps `analytics.read`, unchanged: it is the dashboard's panel and moves with
the dashboard.

Endpoints follow `AnalyticsEndpoints` exactly: `MapMonitoringEndpoints` in the host, versioned group,
`RequireAuthorization(permission)`, `WithName`/`WithSummary`/`WithDescription` on every route,
queries dispatched through `IDispatcher` with `[RequiresPermission]` on the query records, problem
details for 400/401/403/404.

## 7. Out of scope, stated

- Devices, gateways, heartbeats, `DeviceOfflineJob`, `GET /monitoring/devices` — Gateway module.
- `GET /monitoring/logs` and any log-reading abstraction — App Insights, linked from the console.
- `system_metric_snapshots`, a metrics collector job, and CPU/memory history — App Insights; the
  overview's live block is the in-app view AD-11 asks for.
- Outbox lag, retry counts, dead-letter listing, replay — the outbox-processor increment.
- Running, pausing or rescheduling a job from the API.
- Alerting, thresholds, notifications on a failed run — a stale flag in a payload is as far as M6
  goes; who is told, and how, is a notifications-surface question (AD-44).
- The console pages that read any of this — a GymOS-Platform increment; M6 is the API they will read.
- Making the retention window a `platform_settings` row — M7.

## 8. Verification plan

**Unit** (`GymOS.BuildingBlocks.UnitTests`): the host calls `Start` then `Succeed` around a
successful run and `Start` then `Fail` around a throwing one, with the trimmed error; a lock skip
calls neither; a job whose constructor throws is recorded `Failed`; a recorder that throws at `Start`
does not stop the job running and is logged. `JobSchedule.Period` and `Describe()` for both schedule
kinds. The stale rule: the four cases (fresh success, missed period, long-running within grace,
long-running past grace) against a fixed clock. The retention SQL's predicate, as a query test over
seeded rows: the newest per job survives however old.

**Integration** (`GymOS.IntegrationTests`, on the leased-database pattern): a real `JobHost` with a
short-interval job records a `Succeeded` row end to end on SQL Server, with `duration_ms` and
`instance` populated; a failing job records `Failed` with the trimmed message and the loop keeps
ticking; `RetentionJob` against seeded runs 31 and 29 days old removes the first and keeps the second,
and keeps a 400-day-old row that is its job's only row; the three endpoints answer 200 with
`monitoring.read` and 403 without; `/runs` on an unknown name is 404, `limit=500` is clamped, and
`hasMore` flips at the boundary; the overview against a database with **no runs and no outbox rows**
returns every registered job with `lastRun: null`, `oldestMessageAgeSeconds: null`, and 200 — the
empty platform is a valid platform, not an error.

**Live**: compose stack, watch `/monitoring/overview` show `renewal` go `Running` → `Succeeded` on a
tick, then kill the API mid-run of a deliberately slow test job and confirm the `Running` row survives
the restart and reads `stale: true` once the grace passes.
