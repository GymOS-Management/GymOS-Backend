# Runbook: rolling back a deployment

**Read §1 before doing anything.** The application and the database roll back by completely different
mechanisms, and the common mistake — assuming the database follows the application — is the one that
turns a ten-minute recovery into a data-loss incident.

---

## 1. The rule

| | Rollback mechanism | Reversible? |
|---|---|---|
| **Application** | redeploy a previous immutable image SHA | **yes**, in about two minutes |
| **Database** | forward fix, or point-in-time restore | **no**, not by "undoing a migration" |

**EF `Down` migrations are not a production rollback mechanism.** They are generated code that
reverses a schema *shape*; they cannot reverse the loss of a dropped column's data, and a `Down` that
runs against a schema which has since drifted does something nobody predicted. This repository does
not run them in any environment, and no procedure here asks you to.

That leaves one question to answer before you touch anything:

> **Did this deployment apply a migration?**

```bash
# Which migrations exist in the image being rolled back FROM, versus the target SHA
git log --oneline <target-sha>..<current-sha> -- \
  src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations
```

- **No output** → §2. Straightforward: redeploy the old image and stop.
- **Output** → §3. The schema has moved. Deciding whether the old code can run against the new
  schema is the whole job, and it is a judgement, not a command.

---

## 2. Application-only rollback

### 2.1 Identify what is running

```bash
curl -s https://<base-url>/version
# {"version":"0.9.0","commit":"<sha>","environment":"Staging","builtAt":"…"}
```

That `commit` is the image tag, the OCI `revision` label, and the Git commit — one value, three
places, asserted by CI. Trust it over the deployment history: the history says what was *asked for*.

### 2.2 Pick the target

```bash
# Revisions Container Apps still holds, newest first
az containerapp revision list -n gymos-uat-api -g GymOS-UAT \
  --query "reverse(sort_by([].{name:name,image:properties.template.containers[0].image,created:properties.createdTime},&created))" -o table

# Or: images GHCR holds, if the revision is gone
gh api /orgs/<owner>/packages/container/gymos-api/versions --jq '.[].metadata.container.tags[]' | head -20
```

Prefer the previous **known-good** SHA — the last one whose deployment run reached
`Status: UAT READY` / `PRODUCTION LIVE` in its job summary. "The one before this" is not the same
thing if the last two runs both failed.

### 2.3 Roll back

```bash
az containerapp update -n gymos-uat-api -g GymOS-UAT \
  --image ghcr.io/<owner>/gymos-api:<target-sha>
```

Production: prefer the pipeline over a manual `az` call — run **Deploy to Production** with
`workflow_dispatch` and `image_tag=<target-sha>`. It goes through `verify-image`, the approval gate
and the smoke checks, and it leaves a record in the same place every other deployment did. A manual
`az containerapp update` is for when the pipeline itself is what is broken.

### 2.4 Verify — all three, in this order

```bash
curl -s -o /dev/null -w '%{http_code}\n' https://<base-url>/health/ready   # expect 200
curl -s https://<base-url>/version | grep -o '"commit":"[^"]*"'            # expect <target-sha>
```

`200` from readiness plus the *wrong* commit means the rollout did not happen — Container Apps
accepted the update and kept serving the old revision. Not rolled back. Do not stop here.

If readiness returns **503**, read the reason before redeploying anything:

```bash
az containerapp logs show -n gymos-uat-api -g GymOS-UAT --tail 100
```

`503` immediately after a rollback most often means pending migrations — the database is *ahead* of
the code you just deployed. That is §3, and it is the situation §1 asked you to check for first.

---

## 3. Rollback across a migration

The old code is now running against a newer schema. Three cases, and only the first is a rollback.

### 3.1 The migration was additive
A new table, a new nullable column, a new index, a new procedure. **The old code ignores it.** Roll
the application back per §2 and leave the schema alone. The extra objects are harmless; remove them
in a later migration if they are genuinely unwanted.

This is the case the additive-first rule exists to produce, and it is why migrations run *before* the
new image is deployed: for that window the old code is already running against the new schema, so a
migration that breaks it breaks production during its own deployment, rollback or no rollback.

### 3.2 The migration was destructive or incompatible
A dropped or renamed column, a narrowed type, a new `NOT NULL` without a default, a changed procedure
signature the old code calls.

**Do not roll the application back.** Old code plus new schema will fail — often partially, which is
worse than failing outright. Choose:

- **Forward fix** (strongly preferred): a new migration and a new deployment that corrects the
  problem. Rolling forward is almost always faster than restoring, and it is the only option that
  does not lose data written since the deployment.
- **Point-in-time restore**, if the data is already wrong and cannot be corrected forward. That is a
  data-loss decision with a business owner, not an engineering one:
  [`DATABASE-RECOVERY.md`](DATABASE-RECOVERY.md).

### 3.3 The migration failed halfway
The `migrate` job is red and `deploy` never ran, so **the old application is still serving** — you
are not in an outage, you are in a blocked deployment. Do not deploy anything to unblock it.

EF applies each migration in a transaction, so a failed one is rolled back and
`__EFMigrationsHistory` does not record it. Read the job log, fix the migration, and let the pipeline
run again. `/health/ready` reporting unhealthy while migrations are pending is the intended behaviour
in that state, not a second fault.

---

## 4. Rolling back configuration

Environment variables and Container App secrets are **not** in the image and **not** in Git, and the
deploy workflow deliberately never reasserts them — so an image rollback does not roll them back.

If the incident followed a configuration change, that change is the thing to revert, and Container
Apps keeps the previous revision's configuration:

```bash
az containerapp revision show -n gymos-uat-api -g GymOS-UAT --revision <previous> \
  --query 'properties.template.containers[0].env' -o json
```

Secret *values* are not readable this way, by design. Reverting one means knowing what it was.

---

## 5. After any rollback

1. **Say so, in writing** — what was rolled back, from which SHA to which, and whether the schema
   moved. The next person reading `/version` needs to know why it does not match `main`.
2. **`main` and Production now disagree.** Either revert the offending commit on `main` or fix
   forward promptly; leaving them diverged means the next routine deployment silently redeploys the
   thing you just rolled back.
3. **Run the schema comparison** if the schema was involved at all — a forward fix applied under
   pressure is exactly how drift gets introduced:
   `.github/workflows/schema-drift.yml`, or `./scripts/schema-compare.ps1` locally.
4. Record it in [`docs/STATUS.md`](../STATUS.md) with the date.

---

## 6. What this repository makes impossible

Worth knowing during an incident, so you do not go looking for a switch that does not exist:

- Nothing here can drop or recreate a database over the network. `reset-local-db.ps1` refuses any
  database but `GymOS_Local` on a local server.
- The application cannot apply a migration in any environment — it holds no DDL rights.
- The schema comparison cannot publish. It only ever generates a report and a script.
- A host cannot open another environment's database. `DatabaseEnvironmentGuard`, no bypass switch.
