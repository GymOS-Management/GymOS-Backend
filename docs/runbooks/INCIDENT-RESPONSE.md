# Runbook: incident response

For when something deployed is not working. Ordered by what to establish first, because the most
common wasted hour is spent debugging the application when the deployment never happened.

---

## 1. First four commands

```bash
BASE=https://<base-url>

curl -s -o /dev/null -w 'live:  %{http_code}\n'  $BASE/health/live
curl -s -o /dev/null -w 'ready: %{http_code}\n'  $BASE/health/ready
curl -s $BASE/version
az containerapp logs show -n gymos-uat-api -g GymOS-UAT --tail 100
```

Read them together — the combination is the diagnosis:

| live | ready | Means | Go to |
|---|---|---|---|
| 200 | 200 | The application is healthy. The fault is in a specific request path, not the deployment. | §4 |
| 200 | 503 | The process runs; it cannot serve correct requests. **Almost always the database.** | §3 |
| 200 | 200 but `/version` reports the **wrong commit** | The deployment did not roll out. Not an application fault. | §2 |
| — | — | Nothing answers. The container is not running or ingress is broken. | §2 |

`/health/live` runs **no checks** by design, so a 200 from it means only "the process is up". It is
never evidence that anything works.

---

## 2. Nothing answers, or the wrong build answers

```bash
# Is a revision actually running?
az containerapp revision list -n gymos-uat-api -g GymOS-UAT \
  --query "[].{name:name,active:properties.active,state:properties.runningState,image:properties.template.containers[0].image}" -o table

# Why did the current one fail to start?
az containerapp logs show -n gymos-uat-api -g GymOS-UAT --tail 200
```

**A container that starts and immediately exits is nearly always a startup guard doing its job**, and
each one names its own cause in the first lines of the log. In rough order of likelihood:

| Log says | Cause |
|---|---|
| `Refusing to start: the 'Staging' environment must use 'GymOS_UAT'…` | `DatabaseEnvironmentGuard` — the connection string points at the wrong environment's database. |
| `…could not be parsed as a SQL Server connection string (Keyword not supported: 'host')` | A PostgreSQL-shaped string. A leftover from before ADR-54. |
| `SigningKey field is required` / shorter than 32 bytes | `Security__Jwt__SigningKey` unset or empty on the app. **An empty environment variable is not an unset one** — it overrides `appsettings`. |
| `TenantIsolationValidator` failing | Row-level security absent — or `VIEW DEFINITION` missing, so the validator cannot see its own policies and fails closed. |
| Login failed for user | Wrong or rotated database credential, or the server firewall. |

If the revision *is* running the wrong image, the deployment was accepted but never took traffic —
that is [`DEPLOYMENT-ROLLBACK.md`](DEPLOYMENT-ROLLBACK.md) §2.4, not a code problem.

---

## 3. Ready returns 503

Readiness includes the database and pending-migration state, so a 503 here has a short list of
causes.

```bash
# What the readiness check itself objected to
az containerapp logs show -n gymos-uat-api -g GymOS-UAT --tail 100 | grep -i -e health -e migration -e database
```

1. **Pending migrations.** The image is ahead of the schema — the `migrate` job failed or was skipped.
   `/health/ready` reporting unhealthy in this state is intended: a whole boot's worth of controls
   (seeders, the isolation validator, both maintenance sweeps) skip themselves while migrations are
   pending, and serving traffic then would be serving it with those controls silently off. Fix the
   migration and let the pipeline run.
2. **The database is unreachable.** Firewall, credential, or the server itself.
   ```bash
   az sql db show -g GymOS-UAT -s gymos-uat -n GymOS_UAT --query 'status' -o tsv
   ```
3. **The database is reachable but throttled.** Check DTU/vCore saturation before assuming a code
   fault: an exhausted service tier looks exactly like a slow application.

---

## 4. The application answers but a request path is wrong

Every request carries a correlation id — supplied in `X-Correlation-Id` or minted, and echoed on the
response. It is on every log line for that request. **Get it from the client first**; it turns "the
console is broken" into a specific request.

```kusto
// Log Analytics, in the Container Apps environment's workspace
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(1h)
| where Log_s contains "<correlation-id>"
| order by TimeGenerated asc
| project TimeGenerated, Log_s
```

Logs outside Development are **compact JSON** deliberately, so they are queryable rather than merely
readable. Useful shapes:

```kusto
// Errors and fatals in the last hour
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(1h) and Log_s has_any ('"@l":"Error"', '"@l":"Fatal"')
| order by TimeGenerated desc

// Slow requests — Serilog's request logging carries Elapsed
ContainerAppConsoleLogs_CL
| where Log_s contains '"Elapsed"'
| extend d = parse_json(Log_s)
| where toreal(d.Elapsed) > 2000
| project TimeGenerated, RequestPath = tostring(d.RequestPath), Elapsed = toreal(d.Elapsed)
| order by Elapsed desc
```

Two things that are **not** defects, and both have cost time before:

- A request the **caller** abandoned logs at a lower level on purpose — a cancelled request is not a
  server error.
- `X-Invitation-Email: disabled` on an invitation response means `Email:Enabled` is false, so nothing
  was sent. The activation link is not recoverable from the logs by design: both messages carry a
  one-time credential, and a log that records one is a second copy of it.

For anything touching a specific business rule, the domain's document under
[`docs/BusinessRules/`](../BusinessRules) states the intended behaviour — check the rule before
concluding the code is wrong.

---

## 5. Audit trail

For "who changed this, and to what", the answer is in the database rather than the logs:

```sql
SELECT occurred_at, actor_user_id, action, entity_type, entity_id, correlation_id
FROM platform.audit_logs
WHERE entity_type = '<type>' AND entity_id = '<id>'
ORDER BY occurred_at DESC;
```

`correlation_id` joins it back to the request logs in §4 — the audit row says what changed, the logs
say what the request was doing.

---

## 6. Deployment and pipeline failures

Read the run's **job summary** first: it names the version, the commit, and which stage failed, which
is faster than reading four job logs to find out.

| Failure | Where to look |
|---|---|
| `migrate` red | Its log. EF wraps each migration in a transaction, so a failed one is rolled back and the old app keeps serving — a blocked deployment, not an outage. |
| `deploy` red | `az containerapp` output; the identity or the resource name. |
| `smoke` red | Readiness never reached 200, or `/version` disagreed. The second means the rollout silently kept the previous revision. |
| Azure login red, `AADSTS700213` | The federated subject. The error quotes the subject actually presented — see [`docs/Infrastructure/AZURE.md`](../Infrastructure/AZURE.md) §1. |
| Schema drift red | [`DATABASE-RECOVERY.md`](DATABASE-RECOVERY.md) §5. |
| CI integration tests failing wholesale | Usually the SQL Server container, not the tests. Check whether it survived; `timeout-minutes` exists because one such run burned 67 minutes. |

---

## 7. Escalation and decision ownership

Three decisions are **not** engineering's alone, and each has a natural owner:

| Decision | Owner |
|---|---|
| Accept data loss to restore an earlier state | the business owner of that data |
| Deploy to Production outside the normal flow | whoever holds Production approval |
| Disable a control (a guard, a policy, a check) to restore service | never done silently — it is a recorded decision with a follow-up |

If a control is genuinely in the way, note that most of them exist because the failure they prevent
already happened once. Prefer fixing the cause; if you cannot, record what was disabled, when, and
what re-enables it.

---

## 8. After the incident

1. Write it down — timeline, cause, what was done. [`docs/STATUS.md`](../STATUS.md) is where this
   project keeps that history, with dates.
2. If a rollback happened, close the divergence between `main` and what is deployed
   ([`DEPLOYMENT-ROLLBACK.md`](DEPLOYMENT-ROLLBACK.md) §5).
3. If the schema was touched under pressure, run the comparison. A hand-applied fix is exactly how
   drift is introduced.
4. If a control failed to catch it, the fix is a test or a guard — not a note in a document telling
   the next person to remember.
