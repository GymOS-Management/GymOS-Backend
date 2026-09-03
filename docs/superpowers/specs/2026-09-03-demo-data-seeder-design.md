# Demo data seeder — Design

**Status: approved 2026-09-03.** Three decisions taken with the owner: the seeder may run in
**Development and UAT** and refuses Production outright; the dataset is a **realistic Cyprus portfolio**
(about a dozen gyms, three plans, every lifecycle state represented, ninety days of platform stats);
and it lands **before M6**, because M6's monitoring page and every existing console are unreadable
against an empty database.

**Scope:** the item `docs/STATUS.md` has carried since 2026-08-24 as *"Demo data is not implemented"*.
`scripts/populate-all.ps1` §5 already names the mechanism and the reason SQL is the wrong tool; this
document records the shape of the data and the rules the seeder obeys, so they are not re-derived.

## 1. What it is for, and what it is not

The Super Admin consoles — dashboard, analytics, gyms, plans, invoices, features — are built and
reviewed, and every one of them renders an empty state on a fresh database. A prospect shown UAT sees
a product with no customers; a developer opening the dashboard sees a chart with no series. The
seeder gives both something true to look at: aggregates created through their own factories and
lifecycle methods, so every row is one the domain would have produced itself.

It is **not** a fixture for tests (the integration suite builds exactly what each test needs), not a
migration (it writes data, never schema), and not a backfill tool (it fills an empty portfolio and
otherwise does nothing — §4).

## 2. Mechanism: a host mode, exactly like the migrator

`Seed:DemoData=true` is read in `Program.cs` after the boot-time seeders (identity catalogue, feature
catalogue, SuperAdmin) have run, so the catalogue the plans reference exists. The host seeds, logs a
summary and **returns 0**, the same shape as `Database:MigrateOnly`. Nothing else starts: no Kestrel,
no jobs. A failure propagates through the same try/catch as the migrator's, logs Fatal and exits
non-zero, so a compose step or pipeline that runs it sees the failure.

It runs as the **application principal**, not the migrator: every write is DML on tables the app
already writes in production, and the invoice number comes from the same stored procedure the API
calls. That is the whole reason it lives inside the application.

`populate-all.ps1` §5 runs it as `docker compose run --rm --no-deps -e Seed__DemoData=true api`
against the already-running local stack, and its exit code is the step's exit code.

## 3. Refusals, in the order they are checked

1. **Production, by either name.** An environment named `Production` (case-insensitive, matching
   `DatabaseEnvironmentGuard`) or a connection whose database is `GymOS_Production` throws before any
   read. There is no override setting, deliberately: a demo portfolio in a production tenant list is
   not a configuration mistake anyone should be able to make with one more flag.
2. **Pending migrations** throw rather than skip. The boot-time seeders skip politely because they
   run on every start; this one runs because an operator asked for it, and an operator who asked
   should learn that the schema is behind rather than be told the seed "finished".

## 4. Idempotence: it fills an empty portfolio and otherwise does nothing

The seeder runs only when the database holds **no gyms and no plans**. Otherwise it logs what it
found and exits 0 without writing. This is stricter than the other seeders' insert-only rule, and
stricter than a marker check on `demo-` slugs, on purpose:

- it can never touch a UAT that has acquired real gyms, whatever their slugs are;
- it never has to reconcile a partially seeded portfolio with a drifted one;
- "did it run?" has one answer — count the gyms.

Re-running is therefore always safe, and `populate-all.ps1` stays idempotent by construction, as
its header promises.

## 5. The portfolio

All times derive from `IClock.UtcNow` at seed time, so the demo is always *recent*: the newest gym
was provisioned two days ago, the trial ends next week, and the overdue invoice is overdue now.
Instants sit at 09:00 UTC on their day so no event straddles a day boundary in the stats.

### Plans — EUR, one currency, as M5 reports it

| Code | Trial | Limits (members / trainers / staff / storage) | Prices | Features on |
|---|---|---|---|---|
| `STARTER` | 14 days | 150 / 3 / 5 / 5 GB | €49 monthly, €490 yearly | reports |
| `GROWTH` | 14 days | 600 / 10 / 15 / 25 GB | €99 monthly, €279 quarterly, €990 yearly | pos, crm, marketing, analytics, reports |
| `ENTERPRISE` | none | unlimited | €249 monthly, €2,490 yearly | everything except `ai_assistant` |

### Gyms — thirteen provisioned, twelve living

| Slug | Plan · cycle | Provisioned | Where it is now |
|---|---|---|---|
| `demo-limassol-iron-works` | Growth · monthly | 170 d ago | Active, every invoice paid |
| `demo-nicosia-apex` | Enterprise · yearly | 160 d | Active, one yearly invoice, paid |
| `demo-larnaca-seaside` | Starter · monthly | 128 d | Active, **current invoice open** (issued days ago, not yet due) |
| `demo-paphos-olympus` | Growth · quarterly | 140 d | **Past due**, current invoice **overdue** |
| `demo-limassol-pulse` | Starter · monthly | 120 d | Active; **override:** `ai_assistant` enabled (pilot) |
| `demo-nicosia-forge` | Growth · monthly | 100 d | Subscription **canceled** 20 d ago, gym still Active |
| `demo-ayia-napa-coastline` | Starter · monthly | 90 d | Subscription **expired** 10 d ago after 30 d past due; gym **Suspended** |
| `demo-limassol-titan` | Enterprise · monthly | 75 d | Active; **override:** `pos` disabled (runs its own) |
| `demo-larnaca-flow` | Starter · yearly | 60 d | Active, yearly invoice paid |
| `demo-nicosia-crossbox` | Growth · monthly | 30 d | Active, trial converted 15 d ago |
| `demo-paphos-harbour` | Growth · monthly | 6 d | **Trialing**, ends in 9 d, no invoice yet |
| `demo-paralimni-eastside` | — | 2 d | **Provisioning**, no subscription |
| `demo-nicosia-old-town` | Starter · monthly | 130 d | Canceled and **deleted** 45 d ago; last invoice **void** |

Population today: 10 Active, 1 Suspended, 1 Provisioning (12 living), 1 Deleted. Subscriptions:
1 Trialing, 7 Active, 1 PastDue, 2 Canceled, 1 Expired. Invoices reach every status the domain has
except Draft, which nothing in the platform creates.

### How each gym is built — through the domain, in order

`Gym.Provision` → `Activate` (a day later) → `GymSubscription.Assign` at activation, which starts a
trial where the plan has one → `ConvertTrial` at the trial's end → one invoice per period, issued at
the later of the period start and the conversion, renewing with `Renew()` until the period containing
the subscription's end (now, or its cancellation/expiry) → invoices settled (§5.1) → `MarkPastDue`,
`CancelNow`, `Expire`, `Suspend`, `Delete` as the row above says. Overrides use
`GymFeatureOverride.Grant` with no user (system), reason included, and differ from the plan so the
redundancy sweep leaves them alone.

#### 5.1 Settling invoices

Every invoice but the last is paid a few days after issue. The last one is: **void** for the deleted
gym; **overdue** (as of the subscription's end) where the subscription is past due or expired;
**paid** for a canceled subscription; and for a healthy one, **paid if its due date has passed**,
otherwise **left open** — which is what makes Seaside's current invoice an open one without a special
case, and what keeps every "paid" invoice's `PaidAtUtc` in the past.

### 5.2 Timestamps are backdated, and this is the one place the seeder steps around a seam

`AuditableTimestampsInterceptor` stamps `CreatedAtUtc` with the clock on every insert, unconditionally.
The stats compiler counts *new gyms* by that column, and a portfolio "created today" would report
thirteen new gyms this morning and none before. So after the aggregates are saved, the seeder sets
`CreatedAtUtc`/`UpdatedAtUtc` with typed `ExecuteUpdate` statements: gyms to their provisioning
instant, subscriptions to their anchor, invoices to their issue date, plans to a date before the
first gym. This touches audit metadata only — no domain invariant reads those columns — and it is
done with EF's typed update rather than SQL text so a renamed column is a compile error.

### 5.3 Ninety days of `daily_platform_stats`, derived from the same timeline

`DailyStatsCompiler` cannot compile history: its population reads are as-of-now, which is exactly the
unwindowed-read caveat M5's review recorded. The seeder therefore does not call it. `DemoTimeline`
derives each day's row from the portfolio itself — status as of the end of that day, gyms provisioned
that day, subscriptions canceled or expired that day, MRR from the subscriptions that were billing —
and records it through `DailyPlatformStats.Record`, whose reconciliation guard (total = active +
suspended + provisioning) holds by construction. Ninety rows, the last for yesterday; the nightly job
compiles today onwards from the real tables, and the two agree because both read the same statuses.

`DemoTimeline` is pure and unit-tested; the seeder is integration-tested against SQL Server.

## 6. What is deliberately not here

- **Members, trainers, classes, bookings, check-ins, payments.** They do not exist in code (Members
  is a frozen design document). The seeder creates nothing it would have to invent.
- **A second currency.** M5 reports MRR per currency with no conversion; a single EUR portfolio is
  what the owner chose to show, and a second currency is one more plan price when it is wanted.
- **`gym_stats`, `storage_used_mb`, `api_requests`, `active_devices`.** Deferred by M5 to their
  sources, which do not exist. The seeder writes the seven columns that do.
- **A "reset" mode.** Deleting demo data is `DELETE` on a development database, which nobody needs a
  host mode for; on UAT it should be a decision, not a flag.
