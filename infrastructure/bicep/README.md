# Infrastructure as code — a skeleton, not yet the source of truth

**Nothing here has ever been applied, and applying it as-is would be a mistake.** Read §2 before
running any deployment command.

The UAT resources were created by hand on 2026-08-24 and work. These templates describe what such an
environment *should* look like, written from the resource names the workflows reference and the
decisions recorded in `docs/Infrastructure/AZURE.md` — **not** from reading the live subscription. The
goal is to reach the point where an environment can be reproduced from Git; this is the first half of
getting there, and the second half is reconciliation, which is a person's job and has not been done.

---

## 1. What is here

```
infrastructure/bicep/
  main.bicep            the environment: parameters, names, and the four modules below
  identities.bicep      id-gymos-api-<env>, id-gymos-migrator-<env> — separate, never combined
  sql.bicep             the SQL server and one database (GymOS_UAT / GymOS_Production)
  container-app.bicep   the Container Apps environment and the API app, with its two probes
  monitoring.bicep      the Log Analytics workspace container logs land in
```

**There is no `acr.bicep`.** GymOS uses **GHCR**, and the pipeline builds, pushes, promotes and pulls
through it today. Adding Azure Container Registry would introduce a resource, an authentication path
and a promotion story for no property the pipeline currently lacks. If it is ever added, the reason
should be a requirement — private networking, or a registry inside the subscription's compliance
boundary — not symmetry with a reference architecture.

---

## 2. Before applying anything: reconcile

The templates and the live resources will differ, and some of those differences are destructive. The
worst one is known and specific:

> **`container-app.bicep` does not declare the application's environment variables or secrets** — the
> connection string, the JWT signing key, the seed administrator. Applying it to the running app
> would **remove** them, and the app would fail its next start on a missing signing key.

That is not an oversight in the template; it mirrors the deploy workflow, which deliberately never
reasserts those values so it cannot undo a correction made against the running app. It does mean the
first real application of this template has to add them — as Container App secrets — before it runs.

Procedure:

```bash
# 1. What exists now, in full. Read it; do not skim it.
az group export -g GymOS-UAT > /tmp/uat-actual.json

# 2. What this template would do. Nothing is changed by what-if.
az deployment group what-if \
  -g GymOS-UAT \
  -f infrastructure/bicep/main.bicep \
  -p environment=uat \
     sqlAdminObjectId='<entra group object id>' \
     sqlAdminLogin='<entra group name>' \
     containerImage='ghcr.io/<owner>/gymos-api:<sha>'

# 3. For every line what-if reports as Modify or Delete, decide which side is right:
#      the template is right  → the resource was configured wrong; fix the resource
#      the resource is right  → the template is wrong; fix the template
#    Repeat until what-if reports only NoChange. Only then is this file the source of truth.
```

Known gaps to expect in step 3, because they were written without reading the live resources: the
database SKU and serverless settings, the Container Apps environment's name and configuration, the
workspace retention, the ingress and scale settings, and the GHCR pull credential (absent here by
design — it is a credential).

**Do not run `az deployment group create` against UAT or Production until what-if is clean.** A first
apply against a hand-built environment is not a deployment; it is an unreviewed migration of every
resource in it.

---

## 3. What these templates will never contain

- **Secrets of any kind.** No SQL administrator password (the server is Entra-only), no connection
  strings, no signing keys, no registry credentials. A template that takes a password parameter is a
  place for a password to be committed.
- **Database schema.** EF Core migrations are the only thing that changes a GymOS database, in every
  environment. `sql.bicep` creates an empty database and stops.
- **Database permissions.** `db_ddladmin` for the migrator, reader/writer/`EXECUTE`/`VIEW DEFINITION`
  for the app, schema ownership, and `READ_COMMITTED_SNAPSHOT` are all T-SQL with no ARM equivalent.
  They are applied by `docs/runbooks/managed-sqlserver-roles.md` and verified by the queries at the
  end of it. Creating a managed identity grants it nothing.
- **The GitHub side.** Environments, secrets, variables, required reviewers and the federated
  credential are configured in GitHub and Entra, not in ARM.

---

## 4. Production

Production has no resources at all yet. When it is created, it should be created **from these
templates** rather than by hand — that is the one environment where reconciliation can be avoided
entirely, by never letting the two representations diverge in the first place.

`main.bicep` takes `environment=prod`, which gives it `GymOS_Production` and the `gymos-prod-*` names.
The remaining steps that are not in any template — the resource group, the GitHub Environment with
required reviewers, the federated credential with `environment:Production` in its subject, and the
`deploy` step in `deploy-production.yml` — are listed in `docs/Infrastructure/AZURE.md` §2.

---

## 5. What remains

| | |
|---|---|
| Reconcile against live UAT until `what-if` is clean | not started — §2 |
| Add the app's environment variables and secrets to `container-app.bicep` | required before any apply |
| Read back the real SKUs, retention and scale settings | not started |
| A Container App **job** for migrations (`gymos-uat-migrate`) | designed, not written — needed for the managed-identity migrator (`docs/Infrastructure/AZURE.md` §5) |
| Production templates exercised | blocked on Production existing |
| A `what-if` check in CI on changes to this directory | worth doing once §2 is clean; pointless before |
