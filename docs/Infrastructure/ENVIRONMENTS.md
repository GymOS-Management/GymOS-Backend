# Environments, databases, and how work reaches them

Three environments, three databases, and no path between them. This document is the whole model.

> **Status, 2026-08-25.** Local development and **UAT are live**. UAT runs on Azure SQL `GymOS_UAT`
> and Container App `gymos-uat-api` in resource group `GymOS-UAT` (`swedencentral`); a push to
> `development` builds, migrates, deploys and smoke-tests with no human step, authenticated by
> federated OIDC with no stored deployment secret. **Production has no Azure resources at all**: its
> workflow is committed, gated off, and its `deploy` step is a deliberate failure. Sections say which
> applies where rather than leaving it to the end. The resource inventory is
> [`AZURE.md`](AZURE.md).

---

## 1. The model

Four stages, three of which own a database. CI owns none, deliberately — its databases are
throwaway Testcontainers instances, so no CI run can reach an environment's data.

```
Development  ─────▶  CI  ─────▶  UAT  ─────▶  Production
   local          every push    development      main
  compose        and every PR     branch        branch
GymOS_Local     (throwaway DBs)  GymOS_UAT   GymOS_Production
```

| | Environment name | Database | Deployed from | Who uses it |
|---|---|---|---|---|
| **Local** | `Development` | `GymOS_Local` | nothing — Docker Compose on the developer's machine | one developer |
| **CI** | (per-test) | generated, throwaway | every push and pull request | nobody; automated gates only |
| **UAT** | `Staging` | `GymOS_UAT` | `development` branch | QA, integration testing, pre-production validation |
| **Production** | `Production` | `GymOS_Production` | `main` branch | real users |

`Staging` is the framework's name for the deployed non-production environment, and UAT is what the
environment is called. Same thing: `ASPNETCORE_ENVIRONMENT=Staging` selects
`appsettings.Staging.json`, and `DatabaseEnvironmentGuard` pairs `Staging` with `GymOS_UAT`.

```
    feature/*  --PR-->  development  ------------>  UAT app  ---->  GymOS_UAT
                             |
                           merge
                             v
                            main  ---(approval)--->  Prod app ---->  GymOS_Production

    developer's machine  -->  docker compose  ---->  local API --->  GymOS_Local
```

**One image crosses those arrows, never two.** Production deploys the exact artifact UAT tested,
tagged by commit SHA; it never rebuilds. See [`../Architecture/DEPLOYMENT.md`](../Architecture/DEPLOYMENT.md) §2.

There is no fourth database, and `staging` is no longer a branch in the flow — see §8.

**The three databases are named differently on purpose.** They could have been one name on three
servers; then the only thing distinguishing production from local would be a hostname buried in a
secret. A name is checkable, and four separate mechanisms check it (§4).

---

## 2. Local development

### Starting it

```
docker compose up -d
```

That is the whole setup step. No `.env` to write, no database to install, no migration to run. In
order, compose brings up:

1. **`mssql`** — SQL Server 2022 Developer, collation `Latin1_General_100_CI_AS`, data in the
   `gymos-mssql-data` volume, health-checked with a real query.
2. **`db-init`** — creates `GymOS_Local` with its collation, turns on `READ_COMMITTED_SNAPSHOT`, and
   creates the `gymos_migrator` and `gymos_app` principals. One-shot: it exits 0 and everything
   downstream is gated on that. Idempotent, so it runs on every `up`.
3. **`migrator`** — applies pending EF migrations as `gymos_migrator`, then exits 0.
4. **`api`** — starts only after the migrator succeeds, connects as `gymos_app`, and seeds identity,
   the feature catalogue and the SuperAdmin on boot.
5. **`mailpit`** — SMTP sink, so the invitation flow works with nothing started by hand (ADR-53).

Sign in at **http://localhost:5080** as `admin@gymos.local` / `gymos-dev-admin-password`.

The frontend is **not** in this stack: the consoles live in their own repositories
(`GymOS-Platform`, `GymOS-Admin`) and talk to this API over HTTP. Run them from there.

### Visual Studio

`docker-compose.dcproj` is in the solution. Set it as the startup project and press F5 — same
services, with the debugger attached to `api`.

### Working with the API on the host

```
dotnet tool restore
docker compose up -d db-init      # brings mssql up with it, creates the database and principals
dotnet run --project src/Host/GymOS.WebApi
```

`docker compose up -d mssql` alone is **not** enough: without `db-init` there is no database and no
logins. `appsettings.Development.json` points the host-run API at `localhost,1433` as `gymos_app`,
so a bare `dotnet run` exercises the same non-owner principal the container does.

---

## 3. Migrations

**The API never migrates, in any environment.** It connects as `gymos_app`, which owns nothing and
holds no DDL rights, so it could not apply a migration even if asked. This is R14's role split, and
it is why `Database:MigrateOnStartup` is `false` in every compose file and every appsettings file.

Migrations run as `gymos_migrator` through the `Database:MigrateOnly` host mode — the process applies
pending migrations, logs, and exits. Same code path everywhere:

| | How migrations run |
|---|---|
| Local | the `migrator` compose service, gated ahead of `api` |
| UAT | a `migrate` job in `deploy-uat.yml`, before the app is deployed |
| Production | a `migrate` job in `deploy-production.yml`, **behind a manual approval gate** |

Nothing in this repository can drop or recreate a database over the network. `MigrateOnly` applies
pending migrations and never deletes; `reset-local-db.ps1` refuses any database but `GymOS_Local` on
a local server (§5).

---

## 4. Isolation — the four things that enforce it

Not one check in one place. Each layer catches what the others cannot.

**1. `DatabaseEnvironmentGuard`, at application startup.** Runs before the migrator branch, so it
covers the migrator too. `Development` owns `GymOS_Local`, `Staging` owns `GymOS_UAT`, `Production`
owns `GymOS_Production`, and a host may not open another environment's database. Failure is fatal —
the host dies rather than serving correct behaviour against the wrong data.

It *denies cross-environment pairings* rather than prescribing a database per environment, and the
difference matters: the integration suite boots the real host as `Development`, `Staging` **and**
`Production` against generated test databases, so a prescriptive rule would have needed a bypass
switch that production could also set. Denying only the dangerous pairings needs no bypass, so there
is none to misuse. Messages never include the connection string, because it carries a password.

**2. `SqlServerFixture`, in the integration suite.** Refuses to create or open any of the three
managed names. The suite already cannot reach a real environment — every database lives in a
throwaway Testcontainers instance — but that argument depends on the container, and the container is
one refactor from being an environment variable.

**3. `Assert-LocalDatabaseTarget`, in the scripts.** Stricter than the application guard: requires
`GymOS_Local` exactly, on a local server, with `ASPNETCORE_ENVIRONMENT` unset or `Development`. The
local-server condition is the one that catches a database *called* `GymOS_Local` sitting on the UAT
server, where the name alone would pass.

**4. The deploy workflows.** UAT refuses to migrate anything not named `GymOS_UAT`; Production
refuses `GymOS_UAT` by name and then requires `GymOS_Production`. Secrets are scoped by GitHub
Environment, so the Production workflow cannot read UAT's secrets and vice versa.

---

## 5. Local database scripts

Both live in `scripts/` and share their safety check (`GymOS.LocalDatabase.psm1`).

### `populate-all.ps1`

```
.\scripts\populate-all.ps1
```

Verifies the target is local → brings up `db-init` and `migrator` → confirms the database answers and
is the one the string named → starts `api` so its seeders run.

**Idempotent by construction, not by intention.** Nothing truncates or deletes: the migrator applies
only pending migrations, and the application's seeders are insert-only by design
(`FeatureCatalogSeeder` adds newly declared keys and leaves operator edits alone; `SuperAdminSeeder`
never resets an existing password). Running it twice is a no-op, not a duplicate.

**What it does not yet do.** Demo data — multiple gyms, plans and prices, subscriptions across
lifecycle states, invoices, per-gym feature overrides, and `daily_platform_stats` rows for the
dashboards — is **not implemented**. The script says so when it finishes rather than exiting silently.

It has to be a seeder inside the application, not SQL from a script: every one of those objects is an
aggregate with invariants enforced in code (`Gym.Provision`, `SubscriptionPlan.Create`,
`GymSubscription.Assign`, `Invoice.Issue`, `DailyPlatformStats.Record`), and invoice numbers come from
a stored procedure the application calls. Inserting past all of that would produce rows the domain
considers impossible. The mechanism to use is the existing host-mode pattern — a `Seed:DemoData` flag
alongside `Database:MigrateOnly`.

Note also what is *already* seeded and genuinely useful: all roles and permissions, the full feature
catalogue, and a SuperAdmin you can sign in as.

### `reset-local-db.ps1`

```
.\scripts\reset-local-db.ps1          # prompts for the database name
.\scripts\reset-local-db.ps1 -Force   # no prompt
```

**This deletes data.** Drops `GymOS_Local`, recreates it via `db-init`, migrates, then calls
`populate-all.ps1`. It connects as `sa` — dropping a database is DDL that neither `gymos_app` nor
`gymos_migrator` is entitled to, which is the role split working — and `SET SINGLE_USER WITH ROLLBACK
IMMEDIATE` first, so a running API does not block the drop.

It refuses `GymOS_UAT` and `GymOS_Production` by name, before opening any connection.

---

## 6. Configuration and secrets

Standard .NET precedence: `appsettings.json` → `appsettings.{Environment}.json` → environment
variables → secret providers. Environment variables use `ConnectionStrings__Database`.

### What is committed, and why that is fine

| File | Contains | Committed? |
|---|---|---|
| `appsettings.json` | non-secret defaults — JWT issuer/audience, Argon2 cost, rate limits | yes |
| `appsettings.Development.json` | the local connection string and a **public** dev JWT key | yes |
| `appsettings.Staging.json` | UAT's non-secret settings. **No connection string, no secrets** | yes |
| `.env.example` | placeholders and documentation | yes |
| `.env` | your local overrides | **no** — gitignored |

The development JWT signing key and the local `Gymos_dev_*` passwords are deliberately public: they
are identical for every developer, loaded only under `Development`, and reach nothing but a container
on `localhost`. Removing them would break one-click startup for no security gain. There is no
`appsettings.Production.json` — production supplies everything through the environment.

`.gitignore` covers `.env`, `.env.*` (with `!.env.example`), `appsettings.*.local.json`, and `*.user`.

### Secrets UAT and Production need

Set these in the **GitHub Environment** of the same name — not as repository secrets, because
environment scoping is what stops one environment reading the other's credentials.

| Secret | Environment | What it is |
|---|---|---|
| `GYMOS_MIGRATOR_CONNECTION_STRING` | UAT, Production | migrator principal — has DDL rights. Used only by the `migrate` job |
| `GYMOS_APP_CONNECTION_STRING` | UAT, Production | app principal — reads and writes rows, no DDL. What the running app uses |
| `GYMOS_JWT_SIGNING_KEY` | UAT, Production | 32+ bytes; `openssl rand -base64 48`. Startup refuses a shorter one |
| `AZURE_CLIENT_ID` | UAT | app registration the deploy job signs in as. **Not a credential** — federated OIDC, so there is no secret to rotate |
| `AZURE_TENANT_ID` | UAT | the Entra tenant. Secret only to keep it out of logs |
| `AZURE_SUBSCRIPTION_ID` | UAT | the subscription `az` acts against. Same |

| Variable | Purpose |
|---|---|
| `UAT_DEPLOY_ENABLED` / `PRODUCTION_DEPLOY_ENABLED` | `true` turns the deploy workflow on. Both are off, so nothing runs against absent infrastructure |
| `UAT_BASE_URL` / `PRODUCTION_BASE_URL` | where the readiness smoke test points |

### How the deploy job authenticates

Federated OIDC, not a stored client secret. The `gymos-uat-deploy` app registration carries a
federated credential whose subject is

```
repo:GymOS-Management@312961276/GymOS-Backend@1322886835:environment:UAT
```

and Entra will exchange a GitHub token only for that exact string.

The numbers are the organisation and repository database IDs. This organisation issues **ID-based**
OIDC subjects, which survive a rename and cannot be impersonated by re-registering the old name — so
the readable `repo:GymOS-Management/GymOS-Backend:...` form is **not** what arrives and registering
it fails with `AADSTS700213`. That error quotes the subject actually presented, which is the quickest
way to read the correct value for a new environment.

Two consequences worth knowing before editing the workflow. The deploy job's `environment: UAT` line
is what produces that subject, so removing it breaks authentication rather than merely widening
scope. And the job needs `permissions: id-token: write`, which is set on the job rather than the
workflow so no other job can mint a token.

Nothing about this expires, so nothing needs rotating. Production will need its own federated
credential, with `environment:Production` in the subject, when it exists.

Configure **required reviewers** on the `Production` environment. That is what makes a production
migration pause for a human.

---

## 7. UAT and Production deployment — UAT live, Production prepared

`deploy-uat.yml` (on push to `development`) and `deploy-production.yml` (on push to `main`).

UAT builds the image, tags it with the commit SHA, pushes to GHCR, migrates, deploys, smoke-tests.
Production **does not rebuild** — it deploys the image UAT already tested, because rebuilding from
the same source produces a different artifact and discards that evidence. It refuses to run at all
if no such image exists.

Both verify the deployment twice: **`/health/ready`** must answer 200 (never `/health/live`, which
passes on an app that cannot serve correct data), and then **`/version`** must report the commit the
run deployed. The second catches a rollout that was accepted and silently kept the previous revision
— the difference between "it is up" and "it is running this commit".

**UAT is implemented and running.** `deploy-uat.yml` deploys to Container App `gymos-uat-api` in
resource group `GymOS-UAT`, and `UAT_DEPLOY_ENABLED` is what turns it on.

**Production's `deploy` step is still a deliberately failing placeholder.** No Azure resource exists
to deploy to, and writing an `azure/webapps-deploy` block against invented resource names would
produce a workflow that looks finished, fails later, and fails less clearly. The file's comment says
what the ten lines become for Container Apps and for App Service.

**Still required before Production can work:** a resource group and an Azure SQL `GymOS_Production`
in an EU region (GDPR — confirm data residency with counsel); the role split applied per the roles
runbook; a hosting resource; the `Production` GitHub Environment with its secrets **and required
reviewers**; a federated credential carrying `environment:Production`; the `deploy` step; then
`PRODUCTION_DEPLOY_ENABLED`. The ordered checklist is [`AZURE.md`](AZURE.md) §2. Note that SQL Server
Developer edition is free but **non-production only**, so `docker-compose.prod.yml` requires
`MSSQL_PID` to name a licensed edition — Azure SQL folds licensing into the service price, which
usually makes it cheaper than a VM for a small workload.

---

## 8. Branches

```
feature/*  --pull request-->  development  -->  UAT
                                   |
                                 merge
                                   v
                                  main  -->  Production
```

Pull requests **build and test only**; they never deploy. `development` can deploy to UAT and never
to Production. `main` can deploy to Production and never uses UAT secrets.

**`staging` is retired.** It sat between `development` and `main` as a third promotion step that
nothing was ever promoted through — it last moved on 13 August — and a branch CI builds but nobody
merges to goes stale and then fails confusingly. The ref still exists; it is no longer in the flow,
and CI no longer builds it. Other GymOS repositories still trigger on it and have not been changed.

---

## 9. Database tooling

Free Microsoft tooling, and nothing in the application depends on any of it:

- **Azure Data Studio** (Windows/macOS/Linux) — browsing, queries, schema. The usual choice.
- **SQL Server Management Studio** (Windows) — heavier, and the best of these for permissions,
  execution plans and schema comparison.
- **VS Code `mssql` extension** — lightest, good if you already live in VS Code.
- **`sqlcmd` inside the container** — no install at all:
  `docker exec -it gymos-mssql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P Gymos_dev_sa_password1 -C -d GymOS_Local`

Connect to `localhost,1433` as `sa` / `Gymos_dev_sa_password1`, and trust the server certificate —
the sidecar's certificate is self-signed, so verification cannot succeed against it.

There is no database console in the compose stack: pgAdmin was removed with PostgreSQL (it cannot
speak to SQL Server) and the `tools` profile is now empty.

---

## 10. Troubleshooting

**Port 1433 is taken.** A native SQL Server, LocalDB, or another project's container wins the race,
and the symptom is a login failure rather than a port conflict. Set `MSSQL_PORT=1434` in `.env` and
match it in `appsettings.Development.json`, or stop the other service.

**A stale `.env`.** `POSTGRES_*` variables are now unread — inert but misleading. `GYMOS_DB_CONNECTION`
is **no longer honoured**: it used to redirect the local API at a shared external database, which is
exactly what the environment model forbids, so it was removed. Delete the line.

**Changing a password in `.env` did nothing.** `db-init` is idempotent and creates principals only if
absent; it does not rotate. Use `ALTER LOGIN`, or `docker compose down -v` for a throwaway
environment. Note that a bootstrap password may not contain a single quote — `sqlcmd -v` substitution
is textual, so the quote breaks the statement before the server can escape it. It fails loudly at
first boot.

**SQL Server will not start.** Check the password satisfies SQL Server's complexity policy (8+
characters, three of upper/lower/digit/symbol) — the engine refuses to start at all if the `sa`
password fails it. Then `docker compose logs mssql db-init`.

**The API will not start and the log names a database.** That is `DatabaseEnvironmentGuard`. Read the
message: it names the environment, the database it found, and the one it expected.

**Everything is broken and I want to start over.**
`docker compose down -v` (destroys the volume), then `docker compose up -d`. Or
`.\scripts\reset-local-db.ps1`, which keeps the container and rebuilds only the database.

---

## 11. Related

- `docs/Architecture/DEPLOYMENT.md` — the pipeline stage by stage, artifact promotion, the approval gate
- `docs/Architecture/DATABASE.md` — EF migrations vs the database project, and drift detection
- `docs/Architecture/SECURITY.md` — principals, isolation, and where each kind of secret belongs
- `docs/Infrastructure/AZURE.md` — the resource inventory, and what only a human can create
- `docs/Infrastructure/DOCKER.md` — the compose topology, hardening, and per-service detail
- `docs/BusinessRules/TenantIsolation.md` — R14, the principals, and what the policy enforces
- `docs/runbooks/managed-sqlserver-roles.md` — applying the role split to a managed instance
- `docs/runbooks/DEPLOYMENT-ROLLBACK.md` — rolling back an image, and why the database does not follow
- `docs/runbooks/DATABASE-RECOVERY.md` — restores, drift reconciliation, and what is not rehearsed yet
- `docs/runbooks/INCIDENT-RESPONSE.md` — what to read first when something deployed is wrong
- `GymOS-Specification/ADR-CHANGELOG.md` — **ADR-54** (SQL Server) and AD-11 (Azure-first)
