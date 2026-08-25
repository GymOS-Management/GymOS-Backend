# Deployment: how a commit becomes a running system

```
                         ┌──────────────────────────────────────────────┐
  feature/*  ──PR──▶     │  CI: build · tests · schema · DACPAC · image │
                         └──────────────────────────────────────────────┘
                                          │  merge
                                          ▼
                                    development ──────▶  UAT      (automatic)
                                          │              GymOS_UAT
                                        merge
                                          ▼
                                        main   ──────▶  Production (approval gate)
                                                        GymOS_Production
```

A pull request **builds and tests only**. `development` can deploy to UAT and never to Production;
`main` can deploy to Production and never uses UAT secrets. The environment model those two
databases belong to is [`docs/Infrastructure/ENVIRONMENTS.md`](../Infrastructure/ENVIRONMENTS.md);
this document is the delivery half.

---

## 1. The four stages

### Development
Local Docker Compose against `GymOS_Local`. One command, no setup step. See
[`docs/Infrastructure/DOCKER.md`](../Infrastructure/DOCKER.md).

### CI — `.github/workflows/ci.yml`
Runs on every push to `main`/`development` and every pull request against them. Two jobs in
parallel, because they prove different things.

**`build-and-test`**

| Step | Gate |
|---|---|
| Restore, then **build the whole solution** | Warnings are errors. Built before tests because `dotnet test` does not build the host — a broken `Program.cs` must fail here, not hide behind green tests. |
| **Build the database project** | Schema validation: every object parsed against the Azure SQL dialect. Built **by path**, because a solution build silently skips a `.sqlproj`. |
| Upload the DACPAC | Artifact `gymos-database-dacpac`, so any commit is comparable without a rebuild. |
| `dotnet format --verify-no-changes` | Formatting is not a review conversation. |
| **Tests** — unit, architecture, integration | Integration tests run against real SQL Server 2022 via Testcontainers. Results upload as `gymos-test-results` whatever the outcome, because the failing run is the one worth reading. |
| Transitive dependency audit | Banned packages and licences (`docs/Engineering/DEPENDENCY_POLICY.md`). |
| Summary | Version, commit, per-stage ✓/✗ and real test counts in the job summary. |

**`docker`** — builds the image from a clean checkout and starts the whole composed stack with one
command, then asserts health, a real login, the security posture, and that `/version` reports the
commit CI tagged the image with. It duplicates the compile deliberately: that is the price of
catching "works with `dotnet run`, not in a container" here rather than at deployment.

Both jobs carry `timeout-minutes`. Without them a run whose SQL Server container died 50 seconds in
went on to burn 67 minutes discovering that every remaining integration test also could not connect.

### UAT — `.github/workflows/deploy-uat.yml`
Automatic on push to `development`, gated on the repository variable `UAT_DEPLOY_ENABLED`.

```
preflight → build (image, tagged by SHA) → migrate → deploy → smoke → summary
```

- **build** pushes `ghcr.io/<owner>/gymos-api:<commit-sha>` — never `latest`.
- **migrate** pulls that exact image and runs it with `Database__MigrateOnly=true` as the *migrator*
  principal, refusing any target not named `GymOS_UAT` before it connects.
- **deploy** updates Container App `gymos-uat-api` to that image, then waits for the new revision to
  reach `Running` — `az containerapp update` returns when the revision is *accepted*, not when it
  serves traffic, and without the wait the smoke test races the rollout.
- **smoke** waits on **`/health/ready`** (never `/health/live`, which passes on an app that cannot
  serve correct data) and then asserts `/version` reports this run's commit. A rollout that silently
  kept the previous revision passes readiness and fails there — the difference between "UAT is up"
  and "UAT is running this commit".

### Production — `.github/workflows/deploy-production.yml`
On push to `main`, gated on `PRODUCTION_DEPLOY_ENABLED`, and **it does not build.**

```
preflight → verify-image → [APPROVAL] → migrate → deploy → smoke → summary
```

`verify-image` asserts the SHA's image already exists in GHCR and fails with an explanation if it
does not. It runs *before* the gate so a reviewer is never asked to approve a deployment that cannot
happen.

---

## 2. Artifact promotion — one image, tested once

```
development ─▶ build ─▶ gymos-api:abc123 ─▶ UAT ─▶ tests pass ─▶ approval ─▶ Production
                                    └──────────── the same abc123 ────────────────┘
```

**Production deploys the image UAT tested. It never rebuilds.** Rebuilding from the same source
produces a different artifact and discards the evidence that anything was tested — the new image is
*probably* equivalent, and "probably" is what a release process exists to eliminate.

This is why nothing anywhere uses `latest`. `latest` cannot express "the build that passed", and a
rollback target named `latest` is not a target at all.

Three things carry the same commit SHA, and all three are asserted rather than assumed:

| | Where |
|---|---|
| The image tag | `ghcr.io/<owner>/gymos-api:<sha>` |
| OCI image labels | `org.opencontainers.image.revision` |
| The running application | `GET /version` → `"commit"` |

`.dockerignore` excludes `.git`, so the build cannot discover its own commit — it is passed in as
`GIT_SHA`. A dropped build argument would leave `/version` answering with no commit at all, which is
why CI asserts the value end to end.

To promote a specific older build: run **Deploy to Production** with `workflow_dispatch` and pass
`image_tag`. The smoke check compares against *that* SHA, not the branch head.

---

## 3. Migrations in the pipeline

**The API never migrates, in any environment** — it connects as a principal with no DDL rights, so
it could not if asked. Same code path everywhere; only the trigger differs:

| | How |
|---|---|
| Local | the `migrator` compose service, gated ahead of `api` |
| UAT | the `migrate` job, before deploy |
| Production | the `migrate` job, **behind the approval gate** |

Migrations run **before** the new image is deployed. That ordering makes the additive-first rule a
requirement rather than a preference: for the window between the two, the *old* code is running
against the *new* schema. A migration that drops or renames something the running version still
reads breaks production during its own deployment. Add, backfill, deploy, and only remove in a later
release.

Nothing in this repository can drop or recreate a database over the network. `MigrateOnly` applies
pending migrations and never deletes; `reset-local-db.ps1` refuses any database but `GymOS_Local` on
a local server.

---

## 4. GitHub Environments

| | UAT | Production |
|---|---|---|
| Deploys from | `development` | `main` |
| Trigger | automatic | automatic, then **required reviewers** |
| Secrets | `GYMOS_MIGRATOR_CONNECTION_STRING`, `GYMOS_APP_CONNECTION_STRING`, `GYMOS_JWT_SIGNING_KEY`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | the same set, its own values |
| Variables | `UAT_DEPLOY_ENABLED`, `UAT_BASE_URL` | `PRODUCTION_DEPLOY_ENABLED`, `PRODUCTION_BASE_URL` |

Environment scoping is the mechanism, not the convention: it is what stops one environment reading
the other's credentials. Configuring **required reviewers on `Production`** is what makes a
production migration pause for a human — the `environment: Production` line in the workflow is only
half of it.

Azure authentication is federated OIDC; there is no stored client secret to rotate. The subject Entra
trusts is ID-based:

```
repo:GymOS-Management@312961276/GymOS-Backend@1322886835:environment:UAT
```

The readable `repo:owner/name:…` form is **not** what arrives, and registering it fails with
`AADSTS700213` — an error that quotes the subject actually presented, which is the fastest way to
read the correct value when adding Production.

---

## 5. Rollback

Application rollback is a redeploy of a previous immutable SHA. Database rollback is **not** an EF
`Down` migration, and pretending otherwise is how a bad afternoon becomes a bad week.

The procedure, including which combinations are safe and which need a restore:
[`docs/runbooks/DEPLOYMENT-ROLLBACK.md`](../runbooks/DEPLOYMENT-ROLLBACK.md).

---

## 6. Status

**UAT is live and deploys itself.** Azure SQL `GymOS_UAT` on `gymos-uat.database.windows.net`,
Container App `gymos-uat-api` in resource group `GymOS-UAT` (`swedencentral`), migrated by the
pipeline, authenticated by OIDC with no stored deployment secret.

**Production has no infrastructure yet.** Its `deploy` step is a deliberate `exit 1` with a comment
saying what the ten lines become — an `azure/webapps-deploy` block against invented resource names
would produce a workflow that looks finished, fails later, and fails less clearly. What remains, and
what has to be created by hand: [`docs/Infrastructure/AZURE.md`](../Infrastructure/AZURE.md).
