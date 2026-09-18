# Azure: what exists, what is owed, and what only a human can do

The record of the deployed infrastructure. It is written from what the workflows and runbooks in this
repository actually reference — **not** from a live subscription read — so every value below is
either committed in this repository or marked as needing verification. Where a name is not recorded
here, the command to discover it is.

**There is no infrastructure-as-code for these resources yet.** `infrastructure/bicep/` is a
skeleton with its own README explaining what remains. Until it is reconciled, *this document and the
resource names inside the workflow files are the record*, and both have to be kept in step by hand
when a resource changes.

---

## 1. UAT — live

| | Value | Recorded in |
|---|---|---|
| Resource group | `GymOS-UAT` | `deploy-uat.yml` |
| Region | `swedencentral` | `docs/STATUS.md` |
| SQL server | `gymos-uat.database.windows.net` | `schema-drift.yml` |
| Database | `GymOS_UAT` | enforced in code (`DatabaseEnvironmentGuard`) |
| Container App | `gymos-uat-api` | `deploy-uat.yml` |
| Container registry | **GHCR** (`ghcr.io/<owner>/gymos-api`), not ACR | `deploy-uat.yml` |
| Deploy identity | app registration `gymos-uat-deploy`, federated OIDC, **no client secret** | `docs/Infrastructure/ENVIRONMENTS.md` |
| App scale | **min replicas 0**, max 1, 0.5 vCPU / 1 GiB — scale-to-zero is load-bearing, see §7 | set by hand; `deploy-uat.yml` comment |
| Database compute | `GP_S_Gen5` serverless, 0.5–2 vCores, **Azure SQL free offer** (100,000 vCore-seconds/month, 32 GB), auto-pause 60 min, on exhaustion **bill over-usage** | set by hand, see §7 |

Values not recorded in this repository, and how to read them:

```bash
az containerapp show      -n gymos-uat-api -g GymOS-UAT --query 'properties.managedEnvironmentId' -o tsv
az containerapp show      -n gymos-uat-api -g GymOS-UAT --query 'properties.configuration.ingress.fqdn' -o tsv
az monitor log-analytics workspace list -g GymOS-UAT -o table
az sql db show -s gymos-uat -g GymOS-UAT -n GymOS_UAT --query '{sku:sku.name,maxSize:maxSizeBytes}' -o json
```

**Why GHCR rather than ACR.** The images are already built, pushed and pulled through GHCR by
workflows that work; the registry is not a bottleneck and not a cost. Adding ACR would introduce a
resource, an authentication path and a promotion story for no property the pipeline lacks. If it is
ever added, the reason should be a requirement — private networking, or a registry inside the
subscription's compliance boundary — and not symmetry with Azure documentation.

### The deployment identity

Federated OIDC. The subject Entra trusts is ID-based:

```
repo:GymOS-Management@312961276/GymOS-Backend@1322886835:environment:UAT
```

The numbers are the organisation and repository database IDs. This organisation issues ID-based OIDC
subjects — the form that survives a rename and that a re-registered name cannot impersonate — so the
readable `repo:owner/name:…` form the Microsoft documentation shows is **not** what arrives, and
registering it fails with `AADSTS700213`. That error quotes the subject actually presented, which is
the quickest way to read the right value when registering Production.

Consequences worth knowing before editing a workflow: the `environment: UAT` line **produces** that
subject, so removing it breaks authentication rather than merely widening scope; and the job needs
`permissions: id-token: write`, set per-job so no other job can mint a token.

Nothing about this expires. There is nothing to rotate.

---

## 2. Production — nothing exists

No resource group, no database, no hosting resource. The `deploy` step in
`deploy-production.yml` is a deliberate `exit 1`.

To stand it up, in this order:

1. Resource group `GymOS-Production`, **in the EU** — GDPR data residency; confirm the region choice
   with counsel, not with this document.
2. Azure SQL database **`GymOS_Production`**. The name is not a preference: `DatabaseEnvironmentGuard`
   refuses to boot a `Production` host against anything else, and that mapping is in code precisely so
   an environment variable cannot move it.
3. Apply the role split — [`docs/runbooks/managed-sqlserver-roles.md`](../runbooks/managed-sqlserver-roles.md).
   `db_ddladmin` for the migrator, never `db_owner`; `VIEW DEFINITION` for the app or
   `TenantIsolationValidator` reads its own row-level security as absent and fails closed.
4. Container App `gymos-prod-api` (or App Service — the choice is still open; Container Apps is what
   UAT proved).
5. GitHub Environment **`Production`** with its own secrets **and required reviewers**. The reviewers
   are what make a production migration pause for a human.
6. A federated credential with `environment:Production` in the subject.
7. Implement the `deploy` step, then set `PRODUCTION_DEPLOY_ENABLED=true`.

Note that SQL Server Developer edition is free but **non-production only**; Azure SQL folds licensing
into the service price, which for a small workload is usually cheaper than a VM.

---

## 3. Two grants needed for schema drift detection

`.github/workflows/schema-drift.yml` is written and gated off. It authenticates with the *existing*
OIDC identity and a Microsoft Entra access token, so it needs **no new secret** — but it does need
two changes that only an Azure administrator can make:

```sql
-- 1. Set an Entra admin on the UAT SQL server (portal, or `az sql server ad-admin create`),
--    without which the server accepts no Entra token at all.

-- 2. Then, connected to GymOS_UAT as that admin:
CREATE USER [gymos-uat-deploy] FROM EXTERNAL PROVIDER;   -- the app registration the pipeline signs in as
ALTER ROLE db_datareader ADD MEMBER [gymos-uat-deploy];
GRANT VIEW DEFINITION ON SCHEMA::platform TO [gymos-uat-deploy];
GRANT VIEW DEFINITION ON SCHEMA::outbox   TO [gymos-uat-deploy];
```

`db_datareader` plus `VIEW DEFINITION` is the whole requirement: the comparison reads catalogue
metadata and nothing else. Do **not** grant it more — the workflow cannot write, and an identity that
could would make that guarantee a matter of trust rather than of permission.

Then set the repository variable `SCHEMA_DRIFT_ENABLED=true`. Leave `SCHEMA_DRIFT_ENFORCING` unset
until a few runs have proved it quiet.

---

## 4. Reaching UAT from a developer machine

Occasionally legitimate — reproducing a UAT-only defect — and never routine.

- The Azure SQL server firewall must allow the machine's public IP.
- `ASPNETCORE_ENVIRONMENT=Staging` is the only environment permitted to open `GymOS_UAT`.
- Use the **app** principal, never the migrator one.
- A local `Staging` boot still runs the seeders and both maintenance sweeps against shared UAT data.
  Turn the two sweeps off (`Auditing__Partitions__RunOnStartup=false`,
  `Idempotency__PurgeOnStartup=false`) — the deployed app already does them.

---

## 5. Target state: managed identity, no database passwords

**Designed, not applied.** Today both the app and the migrator authenticate to SQL with passwords
held as GitHub Environment secrets. The target removes them:

```
Container App      gymos-uat-api
  user-assigned    id-gymos-api-uat
  connection       Authentication=Active Directory Managed Identity; User Id=<client-id>
  database rights  db_datareader, db_datawriter, EXECUTE, VIEW DEFINITION      ← no DDL, unchanged

Container App JOB  gymos-uat-migrate                                            ← NEW resource
  user-assigned    id-gymos-migrator-uat
  started by       az containerapp job start   (from the migrate job)
  database rights  db_ddladmin, db_datareader, db_datawriter,
                   ALTER ANY SECURITY POLICY, schema ownership
```

**Why the migrator needs a new resource, and why this is not a five-minute change.** The `migrate`
job runs `docker run` **on a GitHub runner**, which has no Azure identity to present — so
`id-gymos-migrator-uat` cannot be used from there at all. Migrations have to execute *inside* Azure,
as a Container App job the pipeline starts and waits on. That replaces a working deployment path, so
it is a separate, reviewed change rather than a side effect of this one.

What stays exactly as it is: the two-principal split, `db_ddladmin` rather than `db_owner`, the API
holding no DDL, and `Database:MigrateOnly` as the mechanism. Only the credential changes.

Order to apply it in, each step independently verifiable:

1. Create both user-assigned identities.
2. `CREATE USER [id-gymos-api-uat] FROM EXTERNAL PROVIDER;` and grant it exactly what `gymos_app`
   holds. Verify with the queries in the roles runbook.
3. Assign the identity to `gymos-uat-api` and switch its connection string to managed-identity
   authentication. **The app principal first**: it is the higher-traffic credential and the reversible
   half — revert by putting the old connection string back.
4. Only then the migrator: create the Container App job, its identity and grants, and change the
   pipeline to start it.
5. Delete the two connection-string secrets from the GitHub Environment. Not before — a secret
   deleted while something still reads it is an outage.

---

## 6. Observability

Container Apps sends container stdout to the Log Analytics workspace in its environment, and the
application writes **compact JSON** outside Development precisely so that is queryable rather than
merely readable. That is the whole observability stack today, deliberately: an OTLP exporter is wired
and inert (`OpenTelemetry:OtlpEndpoint` empty), so adding Azure Monitor later is configuration rather
than code.

Useful queries and what to read when something is wrong:
[`docs/runbooks/INCIDENT-RESPONSE.md`](../runbooks/INCIDENT-RESPONSE.md).

---

## 7. Manual changes that must be mirrored here

Until Bicep is reconciled, these three drift silently and each has already been a source of
confusion:

1. **Resource names** — duplicated in `deploy-uat.yml`, `schema-drift.yml` and this table.
2. **Container App environment variables and secrets** — the connection string, the JWT key, the seed
   admin. The deploy workflow deliberately does **not** reassert them, because a workflow that
   rewrote them on every push would silently undo any correction made against the running app. So
   they are only ever changed by hand, and nothing in Git records their current values.
3. **Database grants** — applied by runbook, verified by the queries at the end of it.
4. **Container App scale: min replicas 0.** Deployment state, so the deploy workflow does not reassert
   it (same reasoning as item 2); set by hand on 2026-09-18. It is what keeps UAT inside the free
   offer. The offer's 100,000 vCore-seconds a month are sized on the assumption that a serverless
   database pauses when idle, and with a replica always running it never does: `RenewalJob` touches
   the database every 15 minutes, so the 60-minute idle window never opens, and at the 0.5 vCore floor
   the whole month's allowance is ~55 hours — September 2026's was spent by the 3rd, and every `migrate`
   after that failed with **error 42119** ("reached the monthly free amount allowance … paused for the
   remainder"). With zero replicas when nobody is using UAT there are no jobs, the database idles,
   pauses, and the first request wakes both in ~30–60 s. The job host re-baselines from the clock on
   wake and its sweeps derive work from state, so nothing is lost but nightly stats rows for nights
   nobody used UAT. Verify and restore with:

   ```bash
   az containerapp show   -n gymos-uat-api -g GymOS-UAT --query 'properties.template.scale' -o json
   az containerapp update -n gymos-uat-api -g GymOS-UAT --min-replicas 0 --max-replicas 1
   ```

5. **Free-offer exhaustion behaviour: bill over-usage, not auto-pause.** The safety valve for item 4:
   if a busy month does spend the allowance, the database keeps serving and compute past the free
   amount is billed at the serverless rate, instead of UAT going dark until the 1st. With the app
   scaling to zero the over-usage should be near nothing; a deploy wakes the database for one billable
   idle hour, so the cost of a push is roughly one 0.5-vCore hour. Verify and set with:

   ```bash
   az sql db show   -s gymos-uat -g GymOS-UAT -n GymOS_UAT --query '{status:status,freeLimit:useFreeLimit,onExhaustion:freeLimitExhaustionBehavior,autoPauseMin:autoPauseDelay}' -o json
   az sql db update -s gymos-uat -g GymOS-UAT -n GymOS_UAT --free-limit-exhaustion-behavior BillOverUsage
   ```
