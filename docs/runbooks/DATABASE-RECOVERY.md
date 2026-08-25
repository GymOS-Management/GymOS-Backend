# Runbook: database recovery

What to do when the data is wrong, lost, or the schema no longer matches the code. The honest version:
**a GymOS database is recovered by restoring it, not by reversing migrations.**

---

## 1. What recovery actually means here

| Situation | Mechanism | Loses data? |
|---|---|---|
| Bad rows written by a defect | targeted forward correction (a migration, or a command through the API) | no |
| Schema ahead of the deployed code | forward migration, or roll the code forward | no |
| Data destroyed by a migration or a bad script | **point-in-time restore** | yes — everything after the restore point |
| Database deleted | restore from the retained backup | yes — anything after the last recoverable point |
| Drift: an object changed outside the migrations | reconcile to a migration; restore only if data was lost | usually no |

**EF `Down` migrations are not on this list.** They reverse a schema shape and cannot reverse the
data a dropped column held. Nothing in this repository runs them in any environment. Treat a `Down`
as a development convenience and nothing more.

Preferences, in order, and the order matters more than any command below:

1. **Correct forward.** Faster than restoring in nearly every real case, and it loses nothing.
2. **Restore to a new database, compare, then decide.** Restoring *beside* production is not the same
   decision as restoring *over* it, and the first buys you the information to make the second.
3. **Restore over the live database.** Data loss, with a business owner's agreement. Never a
   unilateral engineering decision.

---

## 2. Azure SQL: what you have to work with

Azure SQL takes automated backups and supports point-in-time restore within the retention window.
**Verify both, per database, before you need them** — the retention that matters is the one actually
configured, not the default someone remembers:

```bash
az sql db show -g <rg> -s <server> -n <database> \
  --query '{name:name,earliestRestore:earliestRestoreDate,retentionDays:backupRetentionDays}' -o json

# Long-term retention, if any is configured at all
az sql db ltr-policy show -g <rg> -s <server> -n <database> -o json
```

A restore **always creates a new database**; it never overwrites. That is the property this whole
runbook is built on: the safe path is always available.

---

## 3. Point-in-time restore, beside the live database

```bash
az sql db restore \
  -g GymOS-UAT -s gymos-uat -n GymOS_UAT \
  --dest-name GymOS_UAT_restore_20260825 \
  --time '2026-08-25T09:30:00Z'
```

Pick the timestamp **immediately before** the damaging event. Find it — do not estimate it — from the
audit trail (`platform.audit_logs`), the deployment run's start time, or the application logs.

Then compare before you commit to anything:

```sql
-- Same query against both databases: does the restored copy hold what the live one lost?
SELECT COUNT(*) FROM platform.<table> WHERE <the condition that describes the damage>;
```

A restored copy is also the least dangerous way to answer "what did this row look like yesterday" —
usually the actual question, and it needs no change to the live database at all.

**Do not point the application at the restored copy.** Its name is not one of the three managed names,
so `DatabaseEnvironmentGuard` would allow it — the guard's deliberate gap for scratch databases — and
you would be serving traffic from a database nothing deploys to and nothing migrates. Read from it
with a query tool.

---

## 4. Promoting a restored copy — data loss, deliberately

Only with the owner's agreement, and only after §3 proved the copy holds what is needed.

```
1. Stop writes.        az containerapp update -n <app> -g <rg> --min-replicas 0 --max-replicas 0
2. Rename the live database out of the way (keep it — it holds everything written since the
   restore point, and that is the only copy of it).
3. Rename the restored copy into the managed name (GymOS_UAT / GymOS_Production).
4. Confirm __EFMigrationsHistory matches the deployed image's migrations. If the restore point
   predates a migration, apply the pending ones through the pipeline's migrate job — never by hand.
5. Restore replica counts. Verify /health/ready is 200 and /version reports the expected commit.
6. Write down the window that was lost, in rows and in time.
```

Step 2 is not optional. Deleting the damaged database removes the only record of everything written
between the restore point and now — which is frequently what someone asks for two days later.

---

## 5. Recovering from drift

Drift means the database and the migrations disagree: `.github/workflows/schema-drift.yml` reported
it, or `./scripts/schema-compare.ps1` did.

**The fix is a migration. Never the generated script.** `would-apply.sql` exists to be read: it tells
you what SqlPackage *would* do to make the database match. Running it makes the schema match while
leaving `__EFMigrationsHistory` unaware, so the next deployment's migration either fails or applies
something already there.

```
1. Read would-apply.sql. Establish what actually differs.
2. Decide which side is right:
   - The repository is right (something was changed by hand) → write the migration that brings
     the database to the intended state, verify it locally, deploy it through the pipeline.
   - The database is right (a migration was wrong) → write the corrective migration. Do not edit
     an applied migration; it will not re-run, and it will not match what other environments hold.
3. Update database/GymOS.Database in the SAME commit — see docs/Architecture/DATABASE.md §3.
4. Re-run the comparison. It must come back clean.
```

Note the one class of drift the comparison cannot see: an object present in the database and absent
from the project (`DropObjectsNotInSource=False` — the reasoning, and why it stays that way, is in
`docs/Architecture/DATABASE.md` §4). If you suspect an unexpected object, look for it directly:

```sql
SELECT s.name AS [schema], o.name, o.type_desc, o.create_date
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name IN ('platform','outbox') AND o.is_ms_shipped = 0
ORDER BY o.create_date DESC;
```

Recent `create_date` values on anything you do not recognise is the signal. One precedent:
`platform.should_not_exist`, left behind by the roles runbook's own negative test, found exactly this
way and dropped.

---

## 6. Objects that need care during any recovery

Three objects are runtime state rather than schema, and a recovery that treats them as schema does
real damage:

- **`platform.invoice_number_seq_<year>`** — created on demand by `next_invoice_number`. Dropping or
  resetting one restarts a **legally consecutive** invoice series at 1. After any restore, confirm the
  sequence's current value is at or beyond the highest issued invoice number for that year.
- **`pf_audit_logs_monthly`** — boundaries grow monthly at runtime. Applying a schema comparison's
  `MERGE RANGE` moves audit rows between partitions. Never publish it.
- **`__EFMigrationsHistory`** — the only record of which migrations a database has. Never edit it by
  hand to "make a deployment work"; that hides the disagreement instead of resolving it.

---

## 7. Local development

No recovery procedure needed and none should be invented — the local database is disposable:

```powershell
.\scripts\reset-local-db.ps1          # drops GymOS_Local, recreates, migrates, seeds
docker compose down -v                # or destroy the volume entirely
```

`reset-local-db.ps1` refuses `GymOS_UAT` and `GymOS_Production` **by name, before opening any
connection**, and requires a local server. That is the point of it existing rather than everyone
keeping their own copy.

---

## 8. What is not in place yet

Recorded so it is a known gap rather than an assumption made during an incident:

- **No restore has ever been rehearsed.** An untested backup is a belief. Restoring `GymOS_UAT` to a
  scratch database is free, non-destructive and answers whether §3 works here — worth doing before
  Production exists rather than after.
- **No long-term retention policy is configured** as far as this repository records. The default
  point-in-time window is short; if the business needs older recovery points, that is a policy to set
  deliberately (`az sql db ltr-policy set`).
- **No Production database exists yet**, so §4 has never applied to Production. Every step above
  should be re-read against the real resource when it does.
