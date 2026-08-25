# SaaS hardening increment — handoff, 2026-08-25

Written to be picked up on a different machine. Everything below is in this branch; nothing is in
anyone's head. **Read §1 first: three things are waiting on a decision or an Azure grant, and none of
them is code.**

The work: take the delivery setup from "working project" to production-grade, without rewriting
working architecture. SQL Server stays, EF migrations stay and remain the only thing that changes a
database, the database project stays declarative-and-never-published, and both deploy workflows keep
their shape (`development → UAT`, promote-not-rebuild → Production).

---

## 1. What is owed, in the order it unblocks things

### 1.1 Two Azure grants — unblocks schema drift detection (15 minutes)

`.github/workflows/schema-drift.yml` is written, valid, and **gated off**. It authenticates with the
*existing* deploy identity by federated OIDC and a Microsoft Entra token, so it needs no new secret —
but the UAT SQL server has to accept that identity:

```sql
-- 1. Set an Entra admin on the gymos-uat SQL server (portal, or `az sql server ad-admin create`).
--    Without one the server accepts no token authentication at all.

-- 2. Then, connected to GymOS_UAT as that admin:
CREATE USER [gymos-uat-deploy] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [gymos-uat-deploy];
GRANT VIEW DEFINITION ON SCHEMA::platform TO [gymos-uat-deploy];
GRANT VIEW DEFINITION ON SCHEMA::outbox   TO [gymos-uat-deploy];
```

Then set the repository variable **`SCHEMA_DRIFT_ENABLED=true`**. Leave `SCHEMA_DRIFT_ENFORCING`
unset until a few runs have come back quiet — a check that starts out red teaches everyone to ignore
it. `db_datareader` + `VIEW DEFINITION` is the whole requirement; do not grant more.

Full detail: `docs/Infrastructure/AZURE.md` §3.

### 1.2 Required reviewers on the `Production` GitHub environment (2 minutes)

The workflow's `environment: Production` line is only half the approval gate. Configuring **required
reviewers** in GitHub is what makes a production migration actually pause for a human.

### 1.3 ADR-54 is still `Proposed` (the real gap)

The SQL Server migration is merged, deployed and running UAT, while the ADR authorising it is
unapproved and its "why" is a placeholder. It lives in `GymOS-Specification/ADR-CHANGELOG.md`. No
amount of pipeline polish makes the setup production-grade while the architecture of record
contradicts the architecture in production, and this is the cheapest item here to close. **Only the
author can write the decisive business reason.**

---

## 2. What was built this increment

| | |
|---|---|
| **Versioning** | `VersionPrefix` in `Directory.Build.props` — one hand-edited line, currently **0.9.0**, bumped when a milestone closes. No new tooling. The commit, not the version, identifies a deployment. |
| **`/version`** | Anonymous, unversioned, excluded from the OpenAPI document like the health endpoints. Reports version, full commit SHA, environment, build timestamp — `null` rather than invented values on an unstamped build. |
| **Traceability chain** | Image tag = `org.opencontainers.image.revision` label = `/version`'s `commit`. `.dockerignore` excludes `.git`, so the Dockerfile takes `GIT_SHA`/`BUILD_TIMESTAMP` as build args, and **CI asserts all three agree**. |
| **Drift detection** | `schema-drift.yml`: build the database project → DACPAC → `DeployReport` against UAT → PASS/FAIL summary → artifacts. Read-only and structurally incapable of publishing. Weekday mornings + on migration commits. |
| **Deployment summaries** | CI, UAT and Production all write a job summary in the agreed format (`Build/Migrate/Deploy/Health` ✓, `Status: UAT READY`). Marks come from real step outcomes, never assumed. |
| **Deployed-build verification** | After readiness, UAT and Production assert `/version` reports the commit the run deployed. A rollout that was accepted but silently kept the previous revision now fails instead of reading green. |
| **Runbooks** | `docs/runbooks/DEPLOYMENT-ROLLBACK.md`, `DATABASE-RECOVERY.md`, `INCIDENT-RESPONSE.md`. Rollback is by immutable image SHA; database recovery is by restore and explicitly **not** by EF `Down`. |
| **Reference docs** | `docs/Architecture/{OVERVIEW,DATABASE,SECURITY,DEPLOYMENT}.md` and `docs/Infrastructure/AZURE.md`. Each links to existing detail rather than restating it — no second source of truth, and deliberately **no `docs/ADRs/`**: ADRs live in `GymOS-Specification`. |
| **Bicep skeleton** | `infrastructure/bicep/` — five templates, all compile, **applied to nothing**. |

### Decisions taken (so they are not re-litigated)

1. **Versioning**: hand-edited `VersionPrefix`, not MinVer/GitVersion. Nothing outside the repository
   versions against GymOS, so a release number is a milestone statement, not a compatibility contract.
2. **Drift credential**: Entra grant to the existing OIDC identity, **not** a new read-only
   connection-string secret.
3. **Managed identity**: designed now, applied later (§3.1).
4. **No `acr.bicep`**: GHCR works and is not a bottleneck. ACR would add a resource, an auth path and
   a promotion story for no property the pipeline lacks.
5. **`docs/runbooks/` stays lowercase**. A case-only rename on Windows + Git is a needless hazard.

### Corrections to things that were wrong

- `README.md` claimed `Database:MigrateOnStartup` is `true` in Development and that the API migrates
  itself. It is `false` everywhere — the API holds no DDL rights in any environment. The documented
  host-run setup (`docker compose up -d mssql db-init`) therefore left an unmigrated database and a
  503 from `/health/ready`. It now says `docker compose up -d migrator`.
- `README.md`'s example connection string named a database (`gymos`) that does not exist.
- `ENVIRONMENTS.md` and both deploy workflow headers still said no Azure resources existed. UAT has
  been live since 2026-08-24.
- `database/README.md`'s "what is not done yet" was the drift workflow, which now exists.

---

## 3. Remaining work, largest first

### 3.1 Managed identity for database access — designed, not applied
Target and ordered procedure: `docs/Infrastructure/AZURE.md` §5.

**The catch, and why it is a separate reviewed change:** the `migrate` job runs `docker run` **on a
GitHub runner**, which has no Azure identity to present — so `id-gymos-migrator-uat` cannot be used
from there at all. Migrations have to move *inside* Azure, as a Container App job
(`gymos-uat-migrate`) that the pipeline starts and waits on. That replaces a working deployment path.

Do the **API identity first** — higher-traffic credential, and reversible by putting the old
connection string back. Delete the GitHub secrets **last**; a secret removed while something still
reads it is an outage.

### 3.2 Bicep reconciliation
`infrastructure/bicep/README.md` §2 is the procedure. **Do not `az deployment group create` against
UAT until `what-if` is clean.** Specifically: `container-app.bicep` deliberately does not declare the
app's environment variables or secrets (mirroring the deploy workflow, which never reasserts them),
so applying it as-is would **remove them** and the app would fail its next start on a missing signing
key.

### 3.3 Smaller items
- Rehearse a restore of `GymOS_UAT` into a scratch database. Free, non-destructive, and no restore has
  ever been tested here — an untested backup is a belief. `DATABASE-RECOVERY.md` §3.
- No long-term retention policy is configured as far as this repository records.
- `org.opencontainers.image.version` reads `24.04`, inherited from the Ubuntu base image. Cosmetic;
  a third build arg fixes it.
- Production infrastructure does not exist at all. Ordered checklist: `AZURE.md` §2.

---

## 4. Verification already done — do not redo it

Run in the pinned SDK container (`mcr.microsoft.com/dotnet/sdk:10.0.302`), because this machine has
only **10.0.301** and `global.json` pins 302, so nothing builds natively:

```bash
docker run --rm -v "<repo>:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0.302 \
  bash -lc "dotnet build GymOS.Backend.slnx -c Release"
```

| | Result |
|---|---|
| Solution build | 0 warnings, 0 errors |
| Database project | 0/0, DACPAC produced (11,885 bytes) |
| Unit + architecture tests | **1,137 passed, 0 failed** (131 · 434 · 247 · 74 · 98 · 153) |
| `ApiEndpointTests` (incl. 2 new `/version` tests) | 21 passed, 0 failed |
| Endpoint-convention + OpenAPI contract/exposure | 14 passed — the frozen contract is unchanged |
| Docker image + container against the compose database | healthy; `/version` → `{"version":"0.9.0","commit":"c8952b4…","builtAt":"…"}` |
| 4 workflows / 5 Bicep templates / `schema-compare.ps1` | valid YAML / all compile / parses cleanly |
| Migrations and `contracts/openapi-v1.json` | byte-identical — untouched |

**Not verified:** the full 314-test integration suite (only the three classes these changes could
affect), and `dotnet format --verify-no-changes` — in a Linux container against a CRLF working tree it
reports 178 whitespace errors in files nobody touched. Zero non-whitespace format errors anywhere,
which is the signal that matters. **Verify formatting once SDK 10.0.302 is installed.**

### Two traps found while verifying, worth remembering
1. **`dotnet test --no-build` exited 0 having run zero tests** in the pinned SDK container. `ci.yml`
   uses that flag, so the new summary step now **fails the run if the test step succeeded with a zero
   test count**. Watch the next CI run.
2. `--logger "trx;LogFileName=tests.trx"` makes every test project overwrite the same file, so the
   counts become "whichever project finished last". The logger is now `--logger trx` (unique names).

---

## 5. Personal to-dos on the machine, not the repository

- **`dotnet user-secrets` for `GymOS.WebApi` still holds a stale Neon Postgres
  `ConnectionStrings:Database`.** It overrides `appsettings.Development.json` and breaks local
  `dotnet run` with `Keyword not supported: 'host'`. Nothing in the repository references Neon.
  ```
  dotnet user-secrets remove "ConnectionStrings:Database" --project src/Host/GymOS.WebApi
  ```
- **Install .NET SDK 10.0.302.** Until then this repository cannot be built, tested or format-checked
  without a container.
- `.claude/skills/neon*` exists locally (untracked, gitignored). Harmless to the repo, but it will
  steer an agent toward Postgres on a SQL Server project.
