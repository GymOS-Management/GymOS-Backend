# GymOS — Implementation Status & Continuation Notes

**Last updated:** 2026-08-25 · **The SQL Server migration is merged, UAT is live and deploys
itself, and the schema is now in Git as a database project.** The ADR that authorises the migration is
still only *proposed* — that gate has not moved. The top section covers picking the SaaS hardening
branch up on a second machine; the one after it covers everything that happened after the merge, and
the paragraphs beyond that describe the migration as it stood on its branch and are kept for their
reasoning, not their branch state.

---

## SaaS hardening branch picked up on the second machine (2026-08-25)

**`feature/saas-hardening` is checked out here, unmerged and with no PR open. Its §5 "personal
to-dos on the machine" turned out to be scoped to the machine the branch was written on — none of
the three applies to this one, verified individually rather than assumed.** All seven repos were
fast-forwarded first; Backend `main` moved to `41c57c9`, Platform `development` and `main` to
`5339c6a`, and `GymOs-Specification` `development` to `fd5c200`, which is where ADR-54 now sits
locally.

**The SDK blocker does not exist here, and the reason is `global.json` rather than an install.**
10.0.302 is still not installed; this machine has 10.0.103 and 10.0.400. The pin carries
`rollForward: latestFeature`, which accepts a *higher* feature band, so the 4xx band satisfies a
3xx request and `dotnet --version` resolves **10.0.400** inside the repository. `dotnet build
GymOS.Backend.slnx -c Release` — **0 warnings, 0 errors** in 17.7s, natively. **CI resolves the
same 10.0.400**: run `32788974695` logs `.NET Core SDK with version '10.0.400' is already
installed`, because `actions/setup-dotnet` is pointed at `global-json-file` and the same
roll-forward applies on the runner. Local and CI are therefore on one SDK, so the formatting result
below is evidence about the version CI actually uses. The other machine needed the pinned container
only because 10.0.301 sits *below* the pin'''s floor and roll-forward never goes backward — not
because 10.0.302 is required anywhere.

**Formatting is now verified, closing §4's one deferred gate.** `dotnet format
--verify-no-changes GymOS.Backend.slnx` — **exit 0**. The 178 whitespace errors §4 recorded were a
Linux-container-against-CRLF-working-tree artifact exactly as it suspected, and they do not
reproduce natively. **Confirmed it was not a silent no-op** before believing the exit code, because
that is precisely §4's trap 1: re-run at `--verbosity diagnostic` it reports **"Formatted 0 of 522
files"**, so it inspected the tree and found nothing to change. Two projects cannot be opened by the
format workspace — `docker-compose.dcproj` and `database/GymOS.Database/GymOS.Database.sqlproj`,
neither associated with a language. Neither holds C#, and the `.sqlproj` case is the same blind spot
as the `dotnet build` skip already recorded above.

**The other two machine to-dos were already clean.** `dotnet user-secrets list --project
src/Host/GymOS.WebApi` holds only `Seed:SuperAdmin:Email` and `Seed:SuperAdmin:Password` — the stale
Neon `ConnectionStrings:Database` that breaks host-run `dotnet run` with `Keyword not supported:
'host'` is not present, so nothing was removed. No `.claude/skills/neon*` exists anywhere in the
tree or under the user profile here, so there is no Postgres-flavoured steer for an agent to pick up.

**Still unrun, and unchanged by any of the above: the 314-test integration suite.** Docker
Desktop's Linux engine is not running on this machine (`npipe:////./pipe/dockerDesktopLinuxEngine`
does not answer), so there is no SQL Server container for `SqlServerFixture` to reach. The 1,137
unit and architecture tests were verified on the other machine and were not re-run here.

**§1's three owed items have not moved, and none is code:** the two Entra/SQL grants for
`gymos-uat-deploy` plus `SCHEMA_DRIFT_ENABLED=true`, required reviewers on the `Production` GitHub
environment, and **ADR-54, still `Proposed`** with a placeholder rationale that only the author can
write.

---

## UAT deployment and the database project as built (2026-08-24/25)

**`development` is at `e03f95b`; `main` is 3 commits behind it and `staging` is retired.** Eleven
commits, all green: CI and Deploy to UAT both pass end to end.

**Five real defects, each hidden behind the previous one.** `dotnet ef` could not reach any database:
`AppDbContextDesignTimeFactory` read a `GYMOS_DATABASE` environment variable that appears nowhere else
in the repository, so every command fell through to a `localhost` fallback whose database name
(`gymos`) was also stale. It now reads `ConnectionStrings__Database`, the one variable compose, both
deploy workflows and the host already use. Then: the GHCR image tag carried the organisation's
capitals and the registry refuses those, so Deploy to UAT had never once succeeded; the `migrate` job
never logged in to GHCR and so could not pull the image `build` had just pushed; and one integration
test asserted the OpenAPI default for Staging while `appsettings.Staging.json` — added in the last
commit of the migration itself — deliberately publishes the document. That last one was invisible
because the first CI run after the merge lost its SQL Server container 50 seconds in and reported 584
failures on top of it. Both CI jobs now carry `timeout-minutes`, so a hung run costs minutes rather
than the 67 that one did.

**UAT is real.** Azure SQL `GymOS_UAT` on `gymos-uat.database.windows.net`, migrated by the pipeline;
`gymos_migrator` and `gymos_app` created per `docs/runbooks/managed-sqlserver-roles.md` with schema
ownership transferred and `VIEW DEFINITION` granted (without it `TenantIsolationValidator` reads its
own row-level security as absent and fails closed); a Container App `gymos-uat-api` in resource group
`GymOS-UAT`, `swedencentral`, answering `/health/ready`. A push to `development` now builds, migrates,
deploys and smoke-tests with no human step. **Deployment authenticates by federated OIDC and there is
no stored deployment secret at all** — the client secret was deleted after the first green run. The
federated subject is `repo:GymOS-Management@312961276/GymOS-Backend@1322886835:environment:UAT`: this
organisation issues ID-based OIDC claims, so the readable form the Microsoft documentation shows is
*not* what arrives, and registering that instead fails with `AADSTS700213` — an error that helpfully
quotes the subject actually presented.

**The schema is in Git.** `database/GymOS.Database` is a `Microsoft.Build.Sql` project — 23 tables,
54 indexes, 22 foreign keys, 3 check constraints, 2 schemas, 2 procedures, 2 functions — every file
generated from `dotnet ef migrations script`, never hand-written. **EF migrations remain the only
thing that changes a database; the project is built, compared and read, and never published.**
`database/README.md` is the reference. Three objects are deliberately not comparable and each would be
harmful to publish: `pf_audit_logs_monthly`, whose boundaries come from `SYSUTCDATETIME()` and grow
monthly at runtime (publishing would `MERGE RANGE` and move audit rows between partitions); the
`invoice_number_seq_<year>` sequences created on demand by `next_invoice_number` (dropping them
restarts a legally consecutive invoice series at 1); and `__EFMigrationsHistory` with the two
principals.

**UAT was verified against it, column by column across all 23 tables: no schema differences at all.**
The only discrepancies were SSDT's scripting style — `nvarchar (100)` for `nvarchar(100)`,
`datetimeoffset (7)` for its own default precision. That comparison also turned up
`platform.should_not_exist` in UAT: the runbook's deliberate negative test, which had succeeded
because it was run on the admin connection rather than as `gymos_app`. Debris rather than a privilege
defect; dropped.

**Two traps worth knowing.** `dotnet build` on `GymOS.Backend.slnx` **silently skips a `.sqlproj`** —
it reports success and produces nothing at all, which is how the DACPAC upload came to fail on a green
build. The project is listed in the solution so Visual Studio shows it and Schema Compare can be run
from it, but CI must build it by path, and does. Second, Visual Studio's Schema Compare defaults to
writing a *second* copy of every object under its own `BySchemaAndSchemaType` layout and rewrites the
`.sqlproj` with explicit `<Build Include>` items; run it source = project, target = database, and use
**Generate Script**, never **Update**.

**Opening the solution in Visual Studio needs the "Data storage and processing" workload** (SQL Server
Data Tools). Without it the IDE cannot load the project type, reports the solution as containing an
incompatible project, and writes an `UpgradeLog.htm` — gitignored for that reason, along with the
`.dbmdl` model cache Visual Studio holds locked while the project is open.

**Still owed on this increment:** the schema-comparison CI workflow, which needs one decision — whether
to give the UAT SQL server an Entra admin so the existing OIDC identity can read it, rather than
adding a connection-string secret. `deploy-production.yml` still has its deliberate `exit 1` deploy
step and will need its own federated credential with `environment:Production` in the subject.

---

**Previously (2026-08-24), while the migration was still on its branch:** **The backend has been
migrated from PostgreSQL 17 to SQL Server 2022, on the branch `feature/sqlserver-migration`, and the
ADR that authorises it is still only *proposed*.**
Two commits by Konstantinos (`4b29293`, a deliberate non-building checkpoint, and `002740f`, which
completes it) replace the Npgsql provider, every runtime adapter, all seventeen migrations, the Docker
stack, and the whole integration harness. The design does not move: single database, shared schema,
`tenant_id` + row-level security, per-module schemas, month-partitioned audit, the outbox, and
optimistic concurrency on every mutable aggregate are all intact — what changes is the mechanism under
each of them (`sp_getapplock` for advisory locks, `sp_set_session_context` for the tenant session,
procedures `WITH EXECUTE AS OWNER` for `SECURITY DEFINER`, `UPDLOCK`/`REPEATABLEREAD` for
`FOR UPDATE`/`FOR SHARE`, a real `rowversion` column for `xmin`, a CI database collation for `ILIKE`).
**`contracts/openapi-v1.json` has no diff**, so no console or mobile caller changes.

**Three things are owed before this can merge, and none of them is code.** First, **ADR-54 is
`Proposed`, not approved** (`GymOS-Specification/ADR-CHANGELOG.md`): the architecture is frozen and
names PostgreSQL in all four documents, so implementation has run ahead of the freeze protocol
(`ENGINEERING_PLAYBOOK.md` step 2). The ADR is drafted and enumerates what it supersedes, what has no
SQL Server counterpart, and what is therefore owed — but **its "why" is a placeholder**: no commit,
doc, or spec in the branch states the decisive business reason, and the author has to fill that in
before anyone can approve it. Second, **the branch has had no review**. Third, **it is cut from
`development`, not `main`** — 62 commits ahead of `main`, of which **60 are the R14/tenancy work from
2026-08-15 that never reached `main`**, so merging it to `main` lands the whole R14 increment at the
same time. `development` is the correct target.

**Gates, as reported by the author in `002740f` and NOT independently re-verified here:** restore,
build, `dotnet format --verify-no-changes` and test all green — **1,420 tests, 0 failures**, including
**314 integration tests** against SQL Server 2022 via Testcontainers — and the composed stack boots
from a clean checkout with the seeded administrator signing in. **Since partly re-verified, and the SDK
gap turned out to have a workaround.** `global.json` pins SDK **10.0.302** (`rollForward:
latestFeature`) and this machine carries only **10.0.301**, so nothing runs locally — but the
**`mcr.microsoft.com/dotnet/sdk:10.0.302`** container is the pinned SDK exactly, and running the
toolchain in there against the mounted repository gave **build 0 warnings / 0 errors**,
**`dotnet format --verify-no-changes` exit 0**, and **1,137 unit tests passed, 0 failed**. Pin that tag
rather than floating `10.0`, which carries 10.0.400 and disagrees about formatting — the command and
that trap are under "Known open items". The pin predates the branch (`fd7763e`); it is an environment
gap, not a regression.
**The 314 integration tests are still unrun — run them before promoting anything.**

**One defect found reviewing the branch, not by running it:**
`src/Host/GymOS.WebApi/appsettings.Development.json` **was never migrated** — it still carries
`Host=localhost;Port=5432;…;Username=gymos_app;Password=gymos_dev_app_password`, which is Npgsql
keyword syntax. `Microsoft.Data.SqlClient` rejects `Host` and `Username` outright, so
`dotnet run --project src/Host/GymOS.WebApi` — and Visual Studio F5 on the API project — fails at
startup on this branch. Nothing caught it because the integration suite builds its own connection
strings in `SqlServerFixture` and the containerised API takes its own from
`docker-compose.override.yml`; the host-run path is the only consumer of that file, and it is exactly
the path "Fresh-machine setup" below documents. **Fixed 2026-08-24** — the file now carries the
container's own app-principal string with `localhost,1433` in place of `mssql,1433`, and the old value
was proved rejected (`SqlConnectionStringBuilder` → "Keyword not supported: 'host'") while the new one
parses. Four smaller items remain open under "Known open items".

**Previously (2026-08-15, the roles' final review):** **The database-roles increment's final review is fixed: invoice
issuance and audit partitioning work again as a role with no DDL rights.** Taking DDL away from the
API process left two plpgsql functions — `platform.next_invoice_number` and
`platform.ensure_audit_log_partitions` — issuing DDL as the *calling* role, so the first invoice of
any year failed with a 500 and audit partition maintenance failed silently and permanently. Both are
now `SECURITY DEFINER` with a pinned `search_path` and `EXECUTE` revoked from `PUBLIC`
(`SecureRuntimeDdlFunctions`), and `/health/ready` reports unhealthy while migrations are pending
instead of green. **Verified through the API against a freshly wiped stack: HTTP 500 / `42501` before,
HTTP 200 and invoice `2026-000001` after.** Gates: build **0 warnings**, `dotnet format` **exit 0**,
**1410 tests passed** (integration **312**). Details in "Database roles — final-review fixes as built".
~~**One thing Members must do first** is now the top entry under "Known open items": `PostgresFixture`
has to move to the migrator/app shape for the whole suite.~~ **That prerequisite is closed** —
`7a75dc5` moved the whole suite, green at 313 — and so are the **two documentation items** the same
final review parked, taken 2026-08-15 at the start of the next piece of work: the readiness check's
remark overstated what it changed, and the managed-Postgres runbook verified `datdba` but never
`proowner` on the two `SECURITY DEFINER` functions. Both are recorded under "Known open items".
**`TenantScope` is now built** (2026-08-15, six unit tests, no production caller yet) — and building it
found that the design's §4 wording could not be honoured: it says "clear on dispose", but
`TenantContext.SetTenant` refuses a re-point and offers no reset by design, so the helper owns a DI
scope per gym instead and lets the scope's death do the clearing. **What Members still needs is one
thing, and it is bigger than it looked:** no gym-scoped grant can be issued through the API — and
issuing one against today's permission evaluator would be a **privilege-escalation defect**, because
`GetEffectivePermissionsAsync` filters on `UserId` alone, so a gym-scoped grant would confer its role's
permissions platform-wide. That is now **designed and approved, not built**
(`docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md`), with a build order that never opens
the window; Members carries it. See "Known open items".

**Previously (2026-08-15, the roles themselves):** **R14's isolation now actually filters — the superuser gap is closed.**
The deployment-half increment (`docs/superpowers/specs/2026-08-15-database-roles-design.md`) replaced
the single superuser connection with three roles: `gymos_admin` (superuser, `POSTGRES_USER`, humans
and maintenance only), `gymos_migrator` (`NOSUPERUSER NOBYPASSRLS`, owns the database/schemas/every
table, the one-shot `migrator` compose service), and `gymos_app` (`NOSUPERUSER NOBYPASSRLS`, owns
nothing, the API at runtime). Created by `docker/postgres/initdb/10-roles.sql`, which reads both
passwords via psql's `\getenv` rather than shell-interpolating them into SQL. Migrations moved out of
the API into the `migrator` service (`Database:MigrateOnStartup` is `false` everywhere, development
included; `api` waits on `migrator`'s `service_completed_successfully`), and
`TenantIsolationValidator`'s connected-role check is now unconditional rather than gated on some entity
implementing `ITenantScoped`. **Re-measured at this close-out, run individually to completion:**
`dotnet build GymOS.Backend.slnx` — 0 warnings, 0 errors. `dotnet format --verify-no-changes
GymOS.Backend.slnx` — exit 0. `dotnet test tests/GymOS.IntegrationTests` — **311 passed**. Whole
solution — `SharedKernel` 131 · `Platform.Domain.UnitTests` 434 · `Platform.Application.UnitTests` 247
· `BuildingBlocks.UnitTests` 66 · `Platform.Infrastructure.UnitTests` 67 · `ArchitectureTests` **153** ·
`IntegrationTests` **311** — **1409 passed, 0 failed**. `docker compose down -v && docker compose up -d`
brought up a working stack from an empty volume: `migrator` applied migrations and exited `0`, `postgres`
and `api` reported healthy, and `SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname LIKE
'gymos%'` showed exactly the three roles with the attributes above. ~~The platform connects to
PostgreSQL as a superuser, which makes `FORCE ROW LEVEL SECURITY` toothless against it~~ — **closed.**
Two things remain open, recorded under "Known open items" below and in
`docs/BusinessRules/TenantIsolation.md`: there is still no way to issue a gym-scoped grant through the
API at all, and ~~`TenantScope` (the design's `using`-shaped per-gym helper) is still owed, not
built~~ — **`TenantScope` was built later the same day; see its entry under "Known open items".** See
"R14 as built" for the mechanism and the new "Database roles (R14 close-out) as built" section below
for this increment. Committed locally on `development`, not yet pushed.

**Previously (2026-08-15, R14's mechanism):** **R14 (tenant isolation) has a subject and a passing
validation for the first time.** `ITenantContext`, the connection-level session interceptor, the startup
`TenantIsolationValidator`, and route-based `TenantResolutionMiddleware` are built and tested —
**303 integration / 1401 total tests green**, 0 build warnings, `dotnet format` clean. **No production
table is tenant-scoped by it; `members` is next and will be the first.** Two things this increment
found rather than fixed: the platform connects to PostgreSQL as a superuser, which makes
`FORCE ROW LEVEL SECURITY` toothless against it, and there is still no way to issue a gym-scoped grant
through the API at all — the first of those two is what the close-out above fixes. See "R14 as built"
for the rest.

**Previously (2026-08-14):** **Every owed review has now run, and 23 of their 44 findings are
fixed.** The five that were outstanding — M4.1's multi-agent gate, M5's, F3's, the activation
increment's console side, and the API reference — plus four fix increments and F4's own review. The
day's arc, if you read nothing else: **the reviews found two live defects in numbers the platform
publishes** (MRR a cent low on 1 annual price in 24, always low; churn counting subscription rows so a
gym churning twice reads as two churned gyms), **both now fixed and both leaving permanently wrong
history**, because `daily_platform_stats` is write-once. See "Next step" for what that means and what
nobody has decided about it.

**The pattern worth carrying forward** is not any single bug: **five separate increments shipped a fix
or a guard that could not work, each with a test that looked like coverage.** An unreachable catch
(`4ce45d0`), a security assertion that `[{}]` satisfies, a rounding test whose fixture divides evenly,
a reconciliation test whose numbers already reconciled — and now R14's pooled-connection leak test,
which passes because Npgsql issues `DISCARD ALL` when a connection returns to the pool, not because
of the interceptor it was written to prove. All passed CI. A test whose fixture cannot exhibit the
failure is indistinguishable from one that can — until somebody checks.

**The fifth one is the instructive one, because it was caught differently.** The first four were found
by reading the test. This one survived its own task review and only fell to the question *"name the
production change that would make this fail"* asked of the finished branch — and even then it was
settled by running the experiment (break the interceptor, watch the test still pass) rather than by
argument, because the reviewer's reasoning about Npgsql could itself have been wrong. Two habits came
out of it and are worth keeping: **ask that question of every guard**, and **when the answer is
reasoned rather than observed, go and observe it.**

· **F4 (feature flags console) is built, ~~and its review is owed~~ — its review has happened, its
three substantive findings are fixed (`2ae0445`), and `F4-AS-BUILT.md` is written.** Three surfaces: the feature catalogue at `/features`, a Features tab on
gym detail, and a Features card on plan detail. Design: `GymOS-Platform/docs/F4-DESIGN.md` · as built:
`GymOS-Platform/docs/F4-AS-BUILT.md` · plan:
`GymOS-Platform/docs/superpowers/plans/2026-08-13-f4-feature-flags-console.md`. **198 unit tests**
across 31 files passing (was 140 across 22 before F4), **66 E2E tests** passing, 0 failed, 0 skipped,
across both the desktop and mobile viewports (was 42 before F4); typecheck, lint and format:check all
clean.

**What F4's review found, in severity order.** **An override's reason could never be corrected** — the
save path filtered submissions on the *value* differing, so an operator who found an override reading
"temp", typed a proper justification and clicked Save got nothing at all: no request, no toast, no
error. The backend supports that write directly (it upserts an existing override with the new reason
while the value still differs from the plan's). This was a **design omission, not an implementation
slip**: D6 says "a reason is required on every value change" and never considered a reason-only edit,
and the reason box was seeded blank, so the stored justification showed in the row above and was
absent from the field that edits it. **The form stayed live during a save** — only the Save button was
gated, so anything typed mid-flight was wiped when the response re-seeded the draft, the same failure
class the whole-branch review had already fixed on the plan card. And **the plan card's stated
permission was wrong**: it is gated on `billing.read` but issues `GET /features`, which the backend
gates on `features.read`, so a principal with one and not the other would take a 403 and see the card
collapse into an error. Latent — no seeded role is in that position, which is why it went unnoticed —
and D9's table has been corrected rather than the code bent to match a wrong document. Four low
findings are recorded in the as-built and deliberately unfixed.

**The whole-branch review before it caught a Critical bug** the nine per-task reviews structurally
could not see: `PlanFeaturesCard` discarded the operator's draft on every save, because `rows` was
rebuilt fresh each render and `isPending` re-rendered the container, tripping the child's render-time
reset back to pre-save state. Fixed with `useMemo`, pinned by a test holding an unresolved promise in
flight.

**`toastApiError` finally landed.** F2's whole-branch review judged the extraction earned at 26
occurrences across 17 files; F3 planned it as its Task 2 and did not do it, so it had grown to 41
across 22 files by the time F4 built on it. All 41 call sites are migrated now. The migration caught a
user-visible regression along the way: two auth screens (`login`, `set-password`) had their fallback
copy flattened from "Something went wrong. Please try again." to "Something went wrong.", losing the
retry advice on the two screens where a user is most likely stuck. Fixed with an optional `fallback`
parameter on `apiErrorMessage` only.

**A design requirement was missing from the plan entirely.** `docs/F4-DESIGN.md` §4 requires
`src/lib/api/problems.ts` to map the `features.*` error codes, and no task's file list assigned that
file — zero codes were mapped until it was caught mid-execution. All seven are mapped now, three of
them commented as console-unreachable defence.

**For whoever reviews F4:** the console cannot predict whether a gym-feature change creates an
exception or withdraws one, because that needs the plan's opinion, and `GET /plans/{id}/features` sits
behind `billing.read` — a permission the features vocabulary deliberately excludes. So a reason is
required on every value change, and the outcome is read back from the response instead of predicted.
That is a deliberate decision (**D6**), not an oversight, and it is the one most likely to be
questioned.

Two things are recorded rather than silently shipped, both wanting a human eye on review: four "X
unavailable" screens now render failures in red with `role="alert"` instead of muted grey, a side
effect of the `ApiErrorText` migration; and the `SourceBadge` tone mapping (Override→warning,
Plan→info, Default→neutral) is an unreviewed design choice.

**Previously (2026-08-13):** **Administrator activation has now had its review, and its five findings
are fixed and merged** (`a74967f`). That was the gate this file called the next thing to run, on the
grounds that it is a security surface that had never had an adversarial pass. What it found, in
severity order: the link's **single-use guarantee did not survive concurrency**; every emailed link
**dropped the console's sub-path**; email being switched off **reported as a successful send**; the
check endpoint **documented a 410 and returned a 404**; and the read-only check **tracked its entity**.
Details in "Known open items" below and in the commit. **1355 tests green** (the concurrency
regression test is the extra one), 0 warnings, format gate 0, CI green on both jobs.

The concurrency one is worth reading even if nothing else here is. `UserActivationToken` was
*explicitly* exempted from optimistic concurrency — in the EF configuration and again in
`ConcurrencyModelTests` — on the reasoning that "two concurrent redemptions inside one transaction
each see the row's real state". Inside one transaction, true; and that is not the case that matters.
Two concurrent *transactions* both read an unconsumed token, both pass `IsUsable`, and both wrote,
because the UPDATE carried no version predicate. `User`'s own xmin did not cover it either: the
password-policy-rejection path commits the consumption **alone**, touching no user row. It was
demonstrated before it was fixed — with the row version reverted the new test reports "no exception
was thrown", the second write committing against an already-consumed token. This is the same class of
defect M4's review turned up: a comment asserting a property the code did not have.

**Read the migration `20260812232206_AddActivationTokenRowVersion` before touching it.** It is
deliberately **empty**: `xmin` is a PostgreSQL system column that already exists on every table, and
the scaffolded `AddColumn` the tooling generated is rejected by the server outright. It exists only
because EF refuses to run with pending model changes.

**Previously (2026-08-12, late):** **Administrator activation is MERGED and works end to end**
(`c487012` here, `d27e32a` in GymOS-Platform) — the newest increment and the one to read first.
`POST /users` had been shipping accounts nobody could use since M1.3: the invite creates a
`PendingActivation` administrator with no password (correctly — §3.6), and the emailed link that was
supposed to activate them never existed, so the only way to give any account a password was the
`Seed:SuperAdmin` path. **ADR-53** (approved, in GymOS-Specification) permits a minimal `IEmailSender`
for exactly two messages rather than building §4.1's Notification Center first, on the grounds that
§4.1's own design makes security a mandatory category that cannot be muted — so these two resolve no
preference and honour no digest. **1354 tests green**, 0 warnings. Verified live through Mailpit and
then in a browser: invite → the first send failed with no SMTP server and the account was still
created (`X-Invitation-Email: failed`) → re-send recovered it → link followed → password set →
**signed in as the invited administrator** → the same link then offers no form. ~~Its review is owed,
and it is a security surface.~~ — that review has since happened; see the entry above. Two smaller things merged alongside: the audit action filter is now
case-insensitive and escapes wildcards (`40b2bb3`), and `F5` starts a stack where the whole flow works
(`0d56184`). See the milestone rows for both.
**M4.1 (the M4 review-fixes increment) is MERGED to `main`**
(`e72b1f8`) — all seven tasks done. M4's first whole-increment review (2026-08-12) found one
Critical, six Important and eight Minor issues; this increment fixes the Critical (a redundant
override was cleaned up only at the instant of its own write, never afterward), all six Importants,
the three contract additions F4's console will need, and the documentation that had claimed things
the code did not do. Rules revised in `docs/BusinessRules/Features.md`. **1339 tests green**
(re-measured on merge), 0 build warnings. The Critical was verified **in both directions** before
merging: fixed on the branch (upgrade a gym onto a plan that grants what an exception granted → the
override is withdrawn, `source` reads `Plan`, platform override count returns to 0) and reproduced on
the pre-merge `main` (the same upgrade left `source: Override` with the stale trial reason) — a
negative control, so the fix is known to fix something. M4.1's multi-agent review gate is still owed;
one finding was added on merge and is recorded under "Known open items" (the nightly sweep logs an
expected concurrency race at `Error`).
**F3 (dashboard & analytics — the frontend share of M5) is MERGED** to GymOS-Platform `main`
(`a504abb`) — designed, approved, built and reviewed-pending; see the F3 row below.
**M5 dashboard & analytics is complete on branch
`m5-dashboard-analytics`** — all six tasks done, business rules recorded in
`docs/BusinessRules/Analytics.md`, verified live against a rebuilt compose stack. **1302 tests green**
(1,051 unit + 251 integration), 0 build warnings. M5 now goes for review as a whole, same as M4 did.
The whole-repository audit is fixed and merged
(`d22d45a`), so the long "awaiting review" backlog (M2, Money, M3.1, M3.2, AD-39, M0.X) is **cleared**.
Only the **API reference** increment, which landed after the audit's scope, is still unreviewed.
**Purpose:** cross-device continuation state. Read this + `GymOS-Specification/README.md`
before resuming work on any machine.

---

## Where we are

| Milestone | State |
|---|---|
| Architecture (ADRs 1–49, frozen) + amendments (ADR-50 .NET 10, ADR-51 superseded, ADR-52 repo map, ADR-53 email sender, **ADR-54 SQL Server — `Proposed`, not approved**) | ✅ in GymOS-Specification · ⬅ **ADR-54 needs its rationale written and then approval; the migration is already implemented** |
| **M0 — engineering foundation** (skeleton, architecture/package law, kernel + CQRS dispatcher, PostgreSQL/EF/outbox, Docker, logging/correlation/OTel, Testcontainers, CI) | ✅ complete |
| **M1.1 — identity domain** (User/Role/Permission/RefreshToken aggregates, platform schema migration, declare-and-validate seeder) | ✅ complete — 69 tests green |
| **M1.2 — auth services & endpoints** (Argon2id, JWT + rotating refresh tokens, `/api/v1/auth`, SuperAdmin seed) | ✅ complete — 159 tests green, verified live |
| **M1.3 — permission pipeline, users/roles endpoints, audit interceptor, OpenAPI diff gate** | ✅ complete + release-reviewed — **256 tests green**, verified live |
| **Pre-M2 hardening** (write convention, optimistic concurrency, business-rule framework, API standards, playbook) | ✅ complete — **337 tests green**, verified live |
| **M2 — gym management** (gyms CRUD, lifecycle, branding, gym detail) | ✅ complete — verified live · **reviewed** in the 2026-08-11 audit (deleted-gym billing leak fixed) |
| **`Money` (AD-42)** — currency-checked arithmetic, explicit rounding, largest-remainder `Allocate`, EF mapping, rule R4 | ✅ complete — storage contract checked against PostgreSQL · **reviewed** in the 2026-08-11 audit (no findings) |
| **Members domain** (business specification, no code) | ✅ **FROZEN** — `docs/Domain/MEMBERS-DOMAIN-DESIGN.md`. 11 product decisions approved 2026-08-05; implementation may begin. 4 legal-review items deferred (mechanism to build, values from counsel). |
| **M3.1 — subscription plan catalogue** (plans, limits, trial, prices; `Money`'s first consumer) | ✅ complete — verified live · **reviewed** in the 2026-08-11 audit (price writes now require `If-Match`) |
| **M3.2 — subscription assignment** (gym_subscriptions, lifecycle, cancellation, payment state) | ✅ complete — verified live · **reviewed** in the 2026-08-11 audit (no findings of its own) |
| **AD-39 idempotency framework** (claim table, pipeline behavior, decision table, host plumbing) | ✅ complete — **reviewed** in the 2026-08-11 audit; its own `FakeClock` time bomb was fixed earlier (`70a65c2`) |
| **M3.3 — invoices** (Invoice aggregate, per-year numbering, issuing on assignment/conversion/upgrade, net-sign proration, mark-paid as the first production `IIdempotentCommand`, void, §3.4 endpoints) | ✅ **merged to `main`** (`e39eb73`) — reviewed at high effort (10 findings, all fixed), **1049 tests green**, verified live incl. idempotent replay |
| **M3.4 — job runner + renewal/overdue jobs** (JobHost + advisory locks, RetentionJob, renewal sweep, overdue sweep with auto-PastDue) | ✅ **merged to `main`** (`bd722bb`) — design approved 2026-08-10, reviewed at high effort (10 findings, all fixed), **1101 tests green**, renewal verified live on the first 15-minute tick |
| **API reference** (OpenAPI bearer scheme + per-operation security, `OpenApi:Enabled` exposure gate, Scalar UI at `/scalar/v1`, browser opens on startup) | ✅ complete — verified live · ⬅ **awaiting review** (Konstantinos's increment, landed after the audit's scope) |
| **Enforced-header declarations** (`Idempotency-Key`, `If-Match`, `X-Confirm-Action` declared as real OpenAPI parameters, with the requiredness the pipeline actually applies) | ✅ **merged to `main`** (`5ee9aef`) — **1123 tests green**, snapshot regenerated and read semantically (25 header parameters added, nothing else changed) |
| **F1 — Super Admin console** (GymOS-Platform repo, `v0.1.0`) | ✅ complete — frontend of M0–M2; 22 unit + 20 E2E green |
| **F2 — billing console** (GymOS-Platform: plans, subscriptions, invoices incl. `Idempotency-Key` minting) | ✅ complete — the frontend of M3; whole-branch reviewed, recorded in `GymOS-Platform/docs/F2-AS-BUILT.md` |
| **F3 — dashboard & analytics console** (GymOS-Platform: `/dashboard` + revenue/growth/churn screens over M5's six operations) | ✅ **merged** (`a504abb`) — **117 unit tests**, owned SVG charts with **no new runtime dependency**, chart palette validated against the repo's own surfaces in both themes · design: `GymOS-Platform/docs/F3-DESIGN.md` · **E2E seed landed 2026-08-13** — `daily_platform_stats` seeded with a deliberate one-day gap by a Playwright `globalSetup`, so D3 is proved end to end; **8/8 analytics specs, both viewports, zero skips** · ⬅ **review owed**, and `F3-AS-BUILT.md` is deliberately unwritten until it can record what the review changed |
| **F4 — feature flags console** (GymOS-Platform: catalogue at `/features`, a Features tab on gym detail, a Features card on plan detail, over M4/M4.1's feature-flags contract) | ✅ **built and reviewed** — **198 unit tests** across 31 files (was 140 across 22 before F4), **66 E2E tests**, 0 failed, 0 skipped, across both desktop and mobile viewports (was 42 before F4) · typecheck/lint/format:check all clean · `toastApiError` finally migrated at all 41 call sites (F2 judged it earned, F3 planned but skipped it) · design: `GymOS-Platform/docs/F4-DESIGN.md` · **as built: `GymOS-Platform/docs/F4-AS-BUILT.md`** · plan: `GymOS-Platform/docs/superpowers/plans/2026-08-13-f4-feature-flags-console.md` · **review done 2026-08-14, 3 of 7 findings fixed (`2ae0445`)** — an override's reason was uncorrectable (a D6 omission, not an implementation slip), the form stayed live mid-save, and the plan card's documented permission contradicted the code. Four low findings recorded in the as-built, deliberately unfixed. **The one review still owed on this increment is nobody's: F4 is closed.** |
| **M0.X — production Docker & Compose** (multi-stage image, dev/prod overlays, hardening, `docs/Infrastructure/DOCKER.md`, CI docker job) | ✅ complete — clean clone → `docker compose up -d` → healthy + working login; production overlay smoke-tested · **reviewed** in the 2026-08-11 audit (no findings; the dev-overlay `0.0.0.0` binding remains a known open item below) |
| **VS Docker Compose orchestration** (`docker-compose.dcproj`, F5 → the real hardened stack, Scalar landing page) | ✅ complete (`ec47eab`, `a42eafa`) — ⚠ Visual Studio rewrites its no-build rule in `GymOS.Backend.slnx` on save, which breaks `dotnet build`/CI with NU1105; untick **Build** for `docker-compose` in Configuration Manager (Debug *and* Release) so VS owns the exclusion |
| **M4 — feature flags** (catalogue + seeder, three-layer resolution, plan contents, per-gym overrides, gym-detail block) | ✅ complete — all six tasks on `main`, **1267 tests green**, verified live · rules in **`docs/BusinessRules/Features.md`** · single-pass reviewed, 2 findings fixed · **multi-agent whole-increment review run 2026-08-12** (1 Critical, 6 Important, 8 Minor) — fixed in **M4.1**, next row |
| **M4.1 — M4 review fixes** (the redundant-override self-cleaning rule now maintained by construction — synchronous on a gym's own subscription change, a nightly sweep for the plan- and default-wide triggers too broad to do inline — plus the contract additions F4's console needs and the write-path/seeder races the review found) | ✅ **merged to `main`** (`e72b1f8`) — all seven tasks, **1339 tests green**, the Critical proved fixed *and* proved to fail on the pre-merge `main` · rules revised in **`docs/BusinessRules/Features.md`** · ⬅ **multi-agent review gate still owed** |
| **M5 — dashboard & analytics** (daily read model + job, live dashboard, three analytics series, `analytics.read`) | ✅ **merged to `main`** — all six tasks (the branch `m5-dashboard-analytics` is history now), **1302 tests green** at merge, verified live · rules in **`docs/BusinessRules/Analytics.md`** · ⬅ **review still owed** · consumed by **F3** |
| **Administrator activation & password-reset email** (`ADR-53`) — the `IEmailSender` seam + SMTP transport, `user_activation_tokens`, invite/re-send/reset endpoints, anonymous `GET`+`POST /auth/set-password`, console `/set-password` | ✅ **merged to `main`** (`c487012`) and to GymOS-Platform (`d27e32a`) — **1354 tests green**, verified live end to end through Mailpit *and* in a browser through to signing in as the invited administrator · design: `docs/superpowers/specs/2026-08-12-admin-activation-email-design.md` · ⬅ **review owed — this is a security surface and has had no adversarial pass** |
| **Audit action filter — case and wildcards** | ✅ **merged** (`40b2bb3`) — `Gym.` used to match nothing and a typed `%` acted as a wildcard; now `ILIKE` with `SearchPatterns.StartsWith`. Proved in both directions (`user.` → 3 entries, `User.` → 0 on the old code) |
| **Solution view + F5** | ✅ **merged** (`053f4d6`, `0d56184`) — Solution Explorer regrouped by role (Host/Platform/Shared/Tests, docker-compose at the root); F5 now starts postgres + api + **mailpit** with email wired, so the invitation flow works with nothing started by hand. No project renamed, moved, or re-referenced |
| **R14 — tenant isolation mechanism** (`ITenantContext`/`ITenantContextSetter`, `TenantSessionInterceptor`, `TenantIsolationValidator`, route-based `TenantResolutionMiddleware`) | ✅ **mechanism built and tested, committed locally on `development`** (not yet pushed to `origin` — pushing is the maintainer's call) — **303 integration / 1401 total tests green**, 0 build warnings, `dotnet format` clean · rules: **`docs/BusinessRules/TenantIsolation.md`** · design: `docs/superpowers/specs/2026-08-14-r14-tenant-isolation-design.md` · **R14 has a subject and a passing validation for the first time, and no production table is tenant-scoped yet** — Members is next and will be the first · three open items originally recorded under "Known open items" — ~~the deployment connects to PostgreSQL as a superuser (voids `FORCE`)~~ **closed by the next row**, no gym-scoped grant can be issued through the API yet, and `TenantScope` is owed rather than built |
| **Database roles (R14 close-out)** — three roles replacing the single superuser (`gymos_admin`/`gymos_migrator`/`gymos_app`), `docker/postgres/initdb/10-roles.sql`, a one-shot `migrator` compose service, `TenantIsolationValidator`'s role check made unconditional | ✅ **built and tested, committed locally on `development`** (not yet pushed) — **311 integration / 1409 total tests green**, `ArchitectureTests` **153**, 0 build warnings, `dotnet format` clean · `docker compose down -v && docker compose up -d` proven to bring up a working stack from an empty volume, migrator exits `0`, three roles present with the correct attributes · design: `docs/superpowers/specs/2026-08-15-database-roles-design.md` · rules: **`docs/BusinessRules/TenantIsolation.md`** · runbook: **`docs/runbooks/managed-postgres-roles.md`** (Azure, not yet provisioned against) · **the superuser open item is closed**; the gym-scoped-grant and `TenantScope` open items are unchanged by this increment |
| **PostgreSQL → SQL Server migration (ADR-54)** — provider and packages, every runtime adapter, all seventeen migrations replaced by two, the Docker stack, and the integration harness | ✅ **merged to `development` and `main`** (`d9fe709`), CI green, and five follow-up defects found and fixed on 2026-08-24 (see the section at the top) — ⬅ **review still owed, ADR-54 approval still owed**. Previously recorded as: **built on `feature/sqlserver-migration`, unmerged and unreviewed** (`4b29293`, `002740f`, Konstantinos, 2026-08-24) — author reports **1,420 tests / 314 integration green** and a booting stack; **not re-verified here** (SDK 10.0.302 missing on this machine) · `contracts/openapi-v1.json` unchanged, so no console/mobile caller moves · **ADR-54 is `Proposed`** and its rationale is a placeholder · branch is cut from `development` and carries the 60 R14 commits with it · console side: `GymOS-Platform` `2c45888` (E2E suite only) · ⬅ **review owed, ADR approval owed** · one host-run defect (`appsettings.Development.json` left on the PostgreSQL string) found and **fixed 2026-08-24** |
| **UAT deployment** (Azure SQL `GymOS_UAT`, Container App `gymos-uat-api`, both database principals, SuperAdmin seeded) | ✅ **live and self-deploying** — a push to `development` builds, migrates, deploys and smoke-tests with no human step · **federated OIDC, no stored deployment secret** · runbook: `docs/runbooks/managed-sqlserver-roles.md` · environment model: `docs/Infrastructure/ENVIRONMENTS.md` |
| **Database project** (`database/GymOS.Database`, `Microsoft.Build.Sql` 2.2.0) — the declarative record of what the migrations produce, plus `scripts/schema-compare.ps1` | ✅ **built, in CI, DACPAC published per green build** — 23 tables / 54 indexes / 22 FKs / 3 checks / 2 procedures / 2 functions, generated from `dotnet ef migrations script` · **verified against `GymOS_UAT` with zero schema differences** · read-only by construction: EF migrations remain the only mutation path · reference: **`database/README.md`** · ⬅ **the comparison CI workflow is still owed** |
| M6+ — per approved roadmap (monitoring, settings) | next |

## M1.2 as built

All five agreed items landed. No schema change was needed — M1.1's `users` and
`refresh_tokens` tables already carried every column, so **there is no new migration**.

1. **Argon2id hasher** — `Argon2PasswordHasher` (Platform.Infrastructure), cost from
   `Security:PasswordHashing`. Hashes are stored in **PHC string format**
   (`$argon2id$v=19$m=…,t=…,p=…$salt$hash`) and verification reads its parameters *out of
   the stored hash*, so raising cost affects new passwords only and never locks anyone out.
2. **JWT access tokens** (`JwtTokenService`, 15 min; claims `sub`, `email`,
   `security_stamp`, `perm_hash`) + **rotating refresh tokens** (`RefreshTokenGenerator`,
   32 random bytes base64url, SHA-256 at rest, httpOnly/Secure/SameSite=Strict cookie
   scoped to `/api/v1/auth`). Replaying a rotated token revokes its **descendants only** —
   other devices hold their own chains and stay signed in.
3. **`/api/v1/auth`** via `Asp.Versioning.Http`: `login`, `refresh`, `logout`, `me`,
   `change-password`. RFC 7807 everywhere, with the stable machine code in a `code`
   extension member. Errors are deliberately coarse (`auth.invalid_credentials` covers
   unknown email, wrong password, and un-activated invitation) so login is not an
   account-existence oracle; the sign-in path hashes even for unknown emails to keep
   response time flat.
4. **Initial SuperAdmin seed** — `SuperAdminSeeder`, config-only, **creates but never
   resets**, refuses a password below the policy, warns and does nothing when unset.
5. **Integration tests** — 32 over real HTTP + real PostgreSQL: full
   login → refresh → reuse-detection → logout, `/me`, change-password, forged token,
   seeder behavior, rate limiting, OpenAPI presence.

### Decisions taken during M1.2 (worth knowing before M1.3)

- **Rate limiting is two mechanisms, not one.** `Security:RateLimiting` caps sign-in
  attempts **per client IP** (ASP.NET fixed window, 429 + `Retry-After`); the domain
  lockout caps them **per account**. A single composite IP+email key would need the
  request body read inside the partition selector, before model binding — buffering every
  sign-in to duplicate protection the lockout already gives. Documented in
  `RateLimitingExtensions`.
- **`IUnitOfWork` + repositories, not `IAppDbContext`.** AD-3's `IAppDbContext` cannot be
  expressed here: `ApplicationLayers_DoNotDependOnAnyInfrastructure` bans
  `Microsoft.EntityFrameworkCore` in Application, so a `DbSet<T>`-typed interface is
  impossible. Write access goes through `IUserRepository`/`IRefreshTokenRepository`, reads
  through `IIdentityQueries` projections, and one `IUnitOfWork.SaveChangesAsync` per use
  case keeps state + outbox + audit in one transaction.
- **OpenAPI paths are substituted.** `AddGymOSOpenApi` rewrites
  `/api/v{version}/…` → `/api/v1/…` and drops the redundant `version` parameter, so
  generated clients and the future diff gate see real paths. **The M1.3 snapshot gate
  should snapshot the transformed document.**
- **Change-password ends every session, the caller's included** — stamp rotation kills
  access tokens, and the handler revokes all refresh tokens so a thief cannot refresh
  back in. Clients must re-authenticate.
- **Deferred deliberately:** rehash-on-login when cost parameters change (needs a domain
  method that upgrades the hash *without* rotating the security stamp, or it would sign
  everyone out); an absolute cap on sliding refresh lifetime (needs a chain-root column);
  `security_stamp` *enforcement* — the claim is issued, but rejecting a stale stamp
  belongs to the M1.3 permission pipeline. Refresh already re-reads account state, so a
  suspension still takes effect within one access-token lifetime.
- **`global.json` now pins SDK `10.0.201`** (was `10.0.301`, which no SDK on this machine
  satisfied — `rollForward: latestFeature` only moves *up*). Machines with 10.0.3xx still
  use it; this is a floor, not a ceiling.

## M1.3 as built

Adds two migrations: `AddAuditLog` (creates `platform.audit_logs`) and `PartitionAuditLogs`
(converts it to monthly range partitions).

1. **Permission pipeline, both layers (AD-5).** Endpoint policies are produced on demand by
   `PermissionPolicyProvider`, so `RequireAuthorization("users.manage")` needs no
   registration list; `AuthorizationBehavior` re-checks the `[RequiresPermission]` on the
   request type. Both read the same `IPermissionEvaluator`, so they cannot disagree.
   Permissions come from the database, not from token claims — a revoked role takes effect on
   the caller's *next request* instead of at token expiry.
2. **Security-stamp enforcement** (closes the M1.2 deferral). `OnTokenValidated` compares the
   token's `security_stamp` against the stored one and refuses a signed, unexpired token
   whose account has changed. Suspension now ends sessions *immediately*, not within one
   access-token lifetime. `ICallerSnapshot` memoizes the read per request, so the validator,
   the endpoint policy, and the pipeline share one query.
3. **`/api/v1/users`** — list (paged/filtered/sorted), detail, invite, update, suspend,
   reactivate, set-roles. **`/api/v1/roles`** — read-only, because seeded role grants are
   reconciled to the code on every startup and an edit endpoint would be overwritten on the
   next deployment. **`/api/v1/audit-logs`** — read-only, permanently.
4. **Audit interceptor (AD-7).** Entity mutations captured in the same transaction as the
   change; sign-in outcomes recorded explicitly via `IAuditTrail`. Auditing is **opt-in per
   entity** (`PlatformAuditedEntities`) and **redaction is opt-out** — any property whose name
   matches `password|secret|token|hash|stamp|signingkey|apikey|credential|privatekey` is
   masked unless explicitly exempted, so a sensitive field added years from now is masked
   without anyone remembering this file exists.
5. **OpenAPI snapshot gate (R13).** `contracts/openapi-v1.json` is committed and diffed by
   `OpenApiContractTests`. Regenerate deliberately:
   `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests`, then read
   `git diff contracts/openapi-v1.json`. It fails on *any* change by design — the review is
   the gate, not a tool guessing which changes are breaking.
6. **Outbox `user_id` is now stamped** from `ICurrentUser` (closes the M1.2 open item).
7. **Audit partitioning** (added by the release review) — monthly range partitions on
   `created_at_utc`, a maintenance function creating months ahead of need, and a DEFAULT
   partition so an audit write can never fail. See `docs/RELEASE-NOTES.md`.
8. **Account lifecycle** (added by the release review) — `UserStatusTransitions` is now the
   single transition table; `User` routes every status change through it. `Locked` and
   `Deleted` are deliberately *not* states, with the reasoning recorded on that type.
9. **Forwarded headers** (added by the release review) — opt-in, topology-declared, off by
   default. Required in any proxied deployment or the rate limiter degrades to one bucket for
   all callers and audit rows all record the proxy's address.

### Decisions and traps found during M1.3

- **A Guid PK that the code assigns must be `ValueGeneratedNever()`.** EF's default for a Guid
  key is `ValueGeneratedOnAdd`, and for an entity discovered through a *navigation* rather
  than an explicit `Add`, EF reads "key already has a value" as "row already exists" and
  tracks it **Modified** instead of **Added**. The UPDATE matches nothing and surfaces as
  `DbUpdateConcurrencyException` — an error that points at optimistic concurrency, which is
  not the problem. Fixed for all entities at once by
  `GymOSModelConventions.ApplyClientAssignedKeys()`, called at the end of `OnModelCreating`.
  **Any new DbContext must call it.**
- **Enum `.ToString()` and record constructors do not belong in an EF projection.**
  `Scope.ToString()` has no SQL translation, and a correlated collection projected straight
  into a positional record constructor does not translate either. Both fail at *runtime*.
  Project into an anonymous type and shape the DTO in memory (see `RoleQueries`).
- **`PermissionDeniedException` is thrown, not returned.** Business failures are `Result`
  values, but reaching a handler without its permission means the *route* is misconfigured —
  a defect. The host maps it to 403 so the caller still sees the right answer, while the
  behavior logs it as an error naming the request.
- **Cross-aggregate invariants live in the Application layer.** "The last active SuperAdmin
  cannot be demoted or suspended" is a constraint over the *set* of users; enforcing it inside
  `User` would mean handing an aggregate a repository. `SuperAdminGuard` centralizes it so
  both callers apply the identical check.
- **Test isolation is not automatic.** xUnit builds a fresh test-class instance per method, so
  a fixed Testcontainers database name silently shares state across a class — and a test that
  counts rows passes or fails on execution order. Suites whose assertions depend on the rows
  present use `PostgresFixture.CreateIsolatedDatabaseAsync`.
- **`WebApplicationFactory` loads the host project's user secrets.** A developer's local
  `Seed:SuperAdmin` therefore leaks into tests; blank the keys explicitly rather than removing
  them from the settings dictionary (`UseSetting` can override, not unset).
- **Known nuance:** `auth.signed_in` audit rows have `user_id` null — at sign-in the caller is
  not yet authenticated, so `ICurrentUser` is empty. The user is identified by `entity_id`
  and `user_email`. Filtering the trail by `userId` will not return sign-ins.

## Pre-M2 hardening as built

Read `docs/M2_READINESS.md` for the full assessment; the short version:

1. **Write responses** return the resource (`201`/`200`), not `204`. One helper, `WriteResults`,
   owns the convention. Three documented exceptions.
2. **Optimistic concurrency** everywhere: `rowVersion` + strong `ETag`, `If-Match` required on PUT
   (`428` without), `409` with `retryable: false` on a stale version, `400` on a malformed one.
   **A write that changes only child rows must call `MarkAggregateChanged`** or the aggregate's
   version never moves — that gap was real and is now covered by a test.
3. **Business rule framework** in SharedKernel (`IBusinessRule`, `BusinessRules`,
   `BusinessRuleViolationException`) plus `BusinessRuleAssert`. `DomainException` is unsealed so a
   broken rule is one exception strategy, not two. Adopted for a real rule, not left speculative.
4. **`docs/BusinessRules/`** — 11 module documents, drafts with assumptions marked `(?)`.
5. **`docs/Engineering/API_STANDARDS.md`** and **`docs/ENGINEERING_PLAYBOOK.md`** are the standing
   references for anything the tests cannot check.
6. **R9 enforced**: `DomainEventConventionTests` checks event naming, versioning, uniqueness, and
   immutability across every production assembly.

## M2 as built

One migration: `AddGyms` (`platform.gyms` + `platform.gym_branding`). Business rules and the deferral
list live in **`docs/BusinessRules/Gyms.md`** — a specification, not a draft.

1. **`Gym` aggregate** (platform schema, **no `tenant_id`** — a gym *is* the tenant, so the row is the
   tenant's definition rather than a row belonging to one). `GymStatusTransitions` is the single
   transition table, tested as an exhaustive matrix. `Deleted` is terminal, per §2.2.
2. **`GymBranding` as its own aggregate**, 1:1 by `GymId`. Branding is edited by different people at
   different times than the gym's commercial details, every field is optional while none of the gym's
   are, and a logo change should not appear in the trail as a change to ownership. It is created empty
   alongside the gym, so no caller needs a create-or-update path.
3. **`/api/v1/gyms`** — list (paged, filtered by status/country/search, deleted excluded by default),
   detail, provision, update, `activate`, `suspend`, `reactivate`, `DELETE`, `PUT /branding`. All seven
   value rules are rule objects with stable codes (`gyms.*`), so a form can highlight the field that
   failed.
4. **`X-Confirm-Action` is now plumbed** (AD-6, its first use): `ConfirmationHeaders.ConfirmAction`
   reads it, and the handler compares it to the gym's slug — a header, not a body field, so it survives
   `DELETE` and so a replayed script that never saw the resource cannot satisfy it.
5. **`Gym` and `GymBranding` are audited** (`PlatformAuditedEntities`). Verified live: the four
   `gym.updated` entries are activate/suspend/reactivate/delete, and the branding-only write correctly
   produced *no* bare-timestamp entry on the gym while recording `gym_branding.updated`.
6. **Permissions**: `gyms.read|create|update|suspend|delete`. `PlatformAdmin` deliberately holds
   suspend but **not** delete — suspension is reversible and deletion is not.

### Decisions and traps found during M2

- **`POST /gyms/{id}/activate` is not in the architecture's endpoint table**, which lists only
  suspend/reactivate/delete. It exists because `Provisioning=1` is a real state in the frozen schema and
  something must move a gym out of it; without it, `Provisioning` would be a dead value no gym could
  leave. When subscription assignment lands in M3 it becomes the natural completion of provisioning.
  This is an addition to a summary table, not a change to a decision — no ADR needed.
- **`DELETE` returns 204, not the soft-deleted resource.** The convention (API_STANDARDS.md §2) says
  `DELETE` → 204, and adding an exception needs a better reason than "the row still exists". `GET` on a
  deleted gym still resolves, which is where an operator sees the recorded deletion.
- **Rules must run on the normalized value, not the raw one.** `Gym.Provision` originally validated
  `owner.Email` before `Normalized()`, so an address pasted with a trailing space was rejected as
  malformed. Caught by a test; the fix normalizes first in both `Provision` and `UpdateDetails`. Worth
  remembering for every future aggregate that trims input.
- **`varchar`, not `char(n)`, for ISO codes.** `bpchar` compares ignoring trailing spaces, so a padded
  value would equal a clean one in an index. Length is a domain rule.
- **`citext` was not adopted for the slug.** Lowercase-by-construction + a unique index + a
  `slug = lower(slug)` CHECK satisfies "unique, URL-safe, immutable" without adding an extension to
  every environment. §2.2 says `citext`; this is an implementation detail meeting the stated property.
- **`dotnet ef migrations remove` needs a reachable database** — it connects to check whether the
  migration was applied. With no database up, restore `AppDbContextModelSnapshot.cs` from git and delete
  the two migration files by hand, or the next `add` produces an empty migration.
- **A hard-coded count in a test is a liability.** `IdentitySeedingTests` asserted SupportAgent had
  exactly 2 permissions and broke when it gained `gyms.read` — a correct change. It now compares against
  `SystemRoles.Definitions`, so it verifies the seeder reproduced the declaration instead of failing
  because a number moved.
- **`Money` (AD-42) was not needed and is still owed before M3.** A gym's currency is an ISO *code*,
  not an amount; nothing in M2 holds a monetary value. Subscriptions do.
- **RLS (R14) was not needed and is still owed with the first tenant-scoped entity.** Gyms are
  platform-scope and carry no `tenant_id`, so there is no policy to write here. Members is where R14
  first has to run.

## `Money` (AD-42) as built

No migration and no consumer yet — deliberately. Built while no table holds a monetary column, because
that is the only time the shape is free to change.

1. **`Currency`** — ISO 4217 code plus **minor units**, which is the fact the code alone does not carry.
   The exception table lists every currency that is *not* hundredths (JPY 0, KWD 3, CLF 4); everything
   else defaults to 2. A test restates that list independently, so two lists must agree.
2. **`Money`** — `readonly record struct`, rounded to minor units on construction, currency-checked
   arithmetic (`CurrencyMismatchException` on EUR + USD), `Multiply` with a **required**
   `RoundingPolicy`, and `Allocate(params int[])` by largest remainder.
3. **`Allocate` is the reason this type exists.** €100 three ways is 33.34/33.33/33.33; the property
   `sum(parts) == whole` is asserted over amounts smaller than the part count, negatives, zero, lopsided
   ratios, and currencies with 0 and 3 decimals. A credit splits as the exact negation of the charge it
   reverses, so the spare cent lands on the same party both times.
4. **Persistence** — `MoneyConventions.HasMoney(…, "total")` maps an EF **complex type** to
   `total_amount numeric(14,4)` + `total_currency varchar(3)`. Currency stores only the code; minor units
   are derived, because a stored copy could go stale and claim EUR has three decimals.
5. **R4 is now enforced** (`MoneyTypeTests`) — its first subject. A `decimal` member named like money
   outside `Money` fails the build. **Verified by introducing a violation and watching it fail**, then
   removing it; an architecture rule nobody has seen fail is a rule nobody knows works.

### Decisions taken while building `Money`

- **`Currency.Of` validates shape, not membership.** Three ASCII letters, no list of currencies that
  exist — a membership check would refuse a real currency the day ISO adds one, and an invented code
  fails everywhere else anyway. The risk worth guarding is a *real* currency with the wrong number of
  decimals, which is what the exception table is for.
- **Arithmetic is methods, comparison is operators.** `a + b` reads as something that cannot fail;
  `a.Add(b)` reads as something with a rule behind it. Comparison has no policy to choose, so `<` stays.
- **No currency-less zero.** `Money.Zero(currency)` requires one, because a currency-less zero would be
  the single value addable to anything — exactly the silent coercion AD-42 exists to stop.
- **A `default(Money)` throws rather than behaving like €0.** A struct field initialises to it with no
  constructor running, and "no currency" must not round or compare.
- **Deferred with reasons:** `ApplyTax` / `TaxRate` / `TaxRoundingPolicy` (AD-42 names them) — the
  per-line-versus-per-total distinction cannot be honoured by a method on a *single* amount, so it
  belongs where invoice lines exist, and the jurisdiction rules need accounting counsel first.
  Intermediate 4-decimal precision likewise waits for its first producer (currency conversion, AD-43);
  the column already has the room, so adding it is additive. `ICurrencyService` and
  `IExchangeRateProvider` (AD-43) are untouched — the first conversion is platform MRR normalization
  in M5.
### ⚠️ The test host on this machine aborts intermittently — read before trusting a test run

`dotnet test` dies with `Internal CLR error (0x80131506)` partway through a run. **Confirmed not ours:**
`git stash -u` back to the last commit reproduces it identically, and it survives
`DOTNET_TieredCompilation=0`. SDK 10.0.201, runtimes 10.0.3 and 10.0.5 both installed.

Two traps it sets:

1. **The crash stack names whichever test was mid-flight**, which looks exactly like a bug in that code.
   It pointed at `UpdateGymBrandingCommandHandler` on one run and at an untouched M1 test on the next.
   Do not debug the named method before reproducing it in a *completed* run.
2. **An aborted run still prints `Passed!` with a truncated count.** `Passed! … Total: 18` on a project
   with 103 tests is a crash, not a pass. **A run is only trustworthy if its output contains no
   `Aborted`** and the total matches the project's known count.

Longer runs fail more often, so the two largest projects (`Platform.Domain.UnitTests` 391,
`Platform.Application.UnitTests` 232) and the slow one (`IntegrationTests` 233, ~2½ min) are the
usual casualties. Verify class by class when it bites:

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~GymApiTests"
```

Full counts per project, for checking a run was complete — **re-measured 2026-08-11 after M5 task 6,
every project run individually to completion with no `Aborted`**: SharedKernel 126 · BuildingBlocks 62
· Architecture 151 · Domain 413 · Application 232 · Infrastructure 67 · Integration 251 = **1302**.
(Was 1267 — SharedKernel/BuildingBlocks/Application/Infrastructure unchanged; Architecture unchanged
too, since M5 added no new theory rows; Domain 394 → 413 and Integration 235 → 251 carry all of M5's
new tests.)

Capping the test connection pools (above) may also have helped here — the last several full runs completed
first time, where they previously needed retries.

## M3.1 as built

One migration: `AddSubscriptionPlans` (`platform.subscription_plans` + `platform.plan_prices`). Rules and
the full deferral list: **`docs/BusinessRules/Subscriptions.md`**.

**M3 is four increments, and that scoping is the main decision here.** Three of its four parts had
prerequisites that did not exist at the time (all since built — the State column is updated):

| Increment | State | Was blocked on |
|---|---|---|
| M3.1 plans + prices | ✅ built | — |
| M3.2 subscription assignment | ✅ built | M3.1 only |
| M3.3 invoices | ✅ built | **AD-39 idempotency framework.** A retried "mark invoice paid" must not pay twice; this is the first genuinely qualifying command the platform has had. |
| M3.4 renewal / overdue jobs | ✅ built | **A job runner.** Nothing ran work on a schedule until M3.4 built one. |

`plan_features` (§2.3) moves to **M4**: it points at the `features` catalogue, which does not exist.

1. **`SubscriptionPlan`** — code (immutable, upper-case identifier), name, description, trial days,
   nullable capacity limits, sort order, and `IsActive`/`IsPublic` as separate questions: inactive means
   "cannot be newly assigned", unlisted means "assignable but not advertised" (a negotiated plan).
2. **`PlanPrice` holds a `Money`** — one row per (cycle, currency). `PUT /plans/{id}/prices` upserts,
   because "the annual euro price is €480" is one intent and making the caller check first invites the
   race where two operators both add a row.
3. **`BillingCycle` values are month counts** (1/3/12), so a period's length is the enum value itself.
4. **Plans are never deleted** — §3.4's "delete = deactivate only". `DELETE /plans/{id}` is 405, asserted.
5. **`billing.read` / `billing.manage`** — `PlatformAdmin` gets read but not manage; `BillingAdmin` gets
   both. What the platform charges is a commercial decision, not day-to-day administration.

### Decisions and traps found during M3.1

- **A `Money` change was silently unaudited — found in the live run, now fixed.**
  `EntityEntry.Properties` **omits complex-type members**; they live under `ComplexProperties`. So a write
  that only changed a price left the interceptor with an empty change set, which its
  "nothing but a timestamp moved" suppression then dropped. A plan repriced €50.00 → €59.99 had **no record
  of who did it** — the exact gap the audit trail exists to prevent, in the exact place I had claimed it
  mattered. The interceptor now walks complex properties recursively and names fields by path
  (`Price.Amount`), which also keeps a future invoice's `Net.Amount` and `Tax.Amount` apart. Pinned by an
  integration test that fails without the fix.
- **EF cannot index inside a complex property.** `HasIndex(..., "Price_Currency")` fails with "no
  corresponding CLR property or field", so the (plan, cycle, currency) unique index is **raw SQL in the
  migration**, documented there and asserted by a test that reads `pg_indexes`. Any future change to the
  price key must be made the same way.
- **`Money` has no wire form, and that is why R4 has three exemptions.** `Currency` keeps its code in a
  private field behind a property that throws when unset, so System.Text.Json would quietly produce a
  default rather than fail. At serialisation boundaries — event payloads, API DTOs — money travels as an
  amount beside an explicit currency code. A `JsonConverter` belongs with the first consumer that needs one.
- **The integration suite outgrew PostgreSQL's connection limit.** Each test host opened a default
  100-connection pool against a container allowing 100 clients; the symptom was
  `53300: sorry, too many clients already` in whichever unrelated test ran when the ceiling was reached.
  Pools are capped at 5 per host and the container now allows 300. Worth remembering as the suite grows.
- **`dotnet run` ignores `ASPNETCORE_URLS`** when `launchSettings.json` is present — it listened on 5080
  while I polled 5081. Pass `--no-launch-profile`, or just use 5080.
- **`launchSettings` aside: a leftover `GymOS.WebApi` process locks the build output.** Two builds failed
  with MSB3027 until it was killed. If a build suddenly cannot copy DLLs, look for a running host first.
- **The verify harness (`verify.sh`) runs `--no-build`** — build first, or it tests stale binaries and
  reports failures that no longer exist. That cost two rounds of confusion.

## M3.2 as built

One migration: `AddGymSubscriptions`. Rules and the deferral list: **`docs/BusinessRules/Subscriptions.md`**.

1. **`GymSubscription`** — one **live** row per gym enforced by a partial unique index
   (`gym_id WHERE status IN (1,2,3)`); ended rows are kept, because they are the record a billing dispute
   is settled from.
2. **Lifecycle** `Trialing → Active ⇄ PastDue`, with `Canceled` and `Expired` terminal and distinct:
   cancellation is a decision (what churn counts), expiry is a failure to collect. Collapsing them would
   make M5's churn figure wrong in a way nobody could untangle.
3. **Seven endpoints** under `/api/v1/gyms/{gymId}/subscription`, plus `subscriptions.manage` — separate
   from `billing.manage`, because deciding what to sell and deciding what one customer pays are different
   authorities.
4. **Two earlier deferrals closed**: gym detail now carries `subscription`, and plan responses carry
   `subscribedGymCount`. Both were omitted while they could only have read null or zero.

### Decisions and traps found during M3.2

- **The spec's "price snapshot FK" cannot be an FK.** §2.3 says `plan_price_id` is the snapshot, but the
  same section's `(plan_id, billing_cycle, currency)` uniqueness forces prices to be *mutated in place* —
  so a pointer would silently reprice every existing subscription, the opposite of "renewals honor the
  price at assignment". The amount, currency, and cycle are **copied**; the FK survives as nullable
  provenance with `ON DELETE SET NULL`. Verified live. **Honours the intent; the literal mechanism is
  unworkable against its own constraint.**
- **Scheduled cancellation must not look like cancellation.** `cancelAtPeriodEnd` leaves the subscription
  `Active` and live — the gym paid to the end of the period. A consumer that revoked access on the
  *scheduling* event would cut off a paying customer, which is why the two are separate events.
- **`PastDue` does not withhold service.** That is a gym suspension. Conflating them would mean every
  failed card ended a business's day.
- **Changing plan updates the one row.** The status enum has no "replaced" state, so writing a `Canceled`
  row for an upgrade would inflate churn. History lives in the audit trail and the event carries both
  plan ids.
- **R4 now encodes its principle rather than a list.** A money-shaped `decimal` is allowed when its type
  is a record *and* declares a currency member. This replaced six by-name exemptions and pre-empted four
  more. **Verified with two decoys** — an aggregate with a `decimal MonthlyFee` is still caught because
  it is a class; a record with an amount and no currency is still caught. Residual risk documented at the
  rule: a domain value object written as a record could pair a decimal with a currency string.
- **Proration moved M3.2 → M3.3** (correcting my own earlier note). `Money.Allocate` is ready, but a
  prorated charge needs an invoice to land on.
- **A leftover `dotnet run` host locks the build output.** Two builds failed with MSB3027 again. If a
  build cannot copy DLLs, look for a running `GymOS.WebApi` first — this has now cost time twice.

## M0.X — production Docker infrastructure as built

Full reference: **`docs/Infrastructure/DOCKER.md`**. Shape: `docker-compose.yml` (environment-neutral
topology, no credentials, no published ports) + `docker-compose.override.yml` (development, applied
automatically) + `docker-compose.prod.yml` (named explicitly, requires real secrets). One image
(`src/Host/GymOS.WebApi/Dockerfile`, 3 stages, 374 MB), everything configured by environment
variables using the application's own key names. Seven services validated healthy: postgres, api,
plus redis / seq / minio / pgadmin / mailpit behind profiles.

`docker compose up -d` from a clean clone is the whole setup step — no `.env`, no database install,
no migration command, ending in a working sign-in as `admin@gymos.local` / `gymos-dev-admin-password`.

### Decisions and traps found during M0.X

- **Compose interpolates each file independently before merging.** A `${VAR:?}` marker in the base
  file cannot be satisfied by an overlay that supplies the value — it aborts the whole command. So
  fail-fast markers live only in `docker-compose.prod.yml`; the base uses `:-`.
- **An empty environment variable is not an absent one, and it outranks `appsettings.json`.**
  `Security__Jwt__SigningKey: ${VAR:-}` in the base blanked the public development key and the API
  refused to start. The base now declares no secrets at all. Same for `Seed__SuperAdmin__*`.
- **`.editorconfig` must be in the build context.** `Directory.Build.props` sets
  `TreatWarningsAsErrors`, and `.editorconfig` is where severities are tuned — omitting it failed the
  image build on CA1716, a rule this repo deliberately turns off.
- **A named volume inherits the ownership of the image directory it covers**, and a directory absent
  from the image is created root-owned. Both writable paths are now created in the Dockerfile before
  `USER`. The failure mode was quiet: DataProtection logged an error, fell back to in-memory keys,
  and the container still reported healthy.
- **`POSTGRES_PASSWORD` applies only at initdb.** Changing it later does nothing to an existing
  volume; the symptom is `password authentication failed`, which looks like a config error.
- **`cap_drop: ALL` breaks more than expected, and quietly.** Redis (`setpriv: setresuid failed`),
  Seq (`/seqsvr/Seq: Operation not permitted` — its binary carries `cap_net_bind_service`, and
  executing a file whose capabilities are outside the bounding set fails with EPERM), and pgAdmin
  (silently fell back from port 80 to 8080, leaving the published port pointing at nothing) all had
  to be booted to be found. Fixes: run redis and seq as their own uid, grant Seq the one capability
  it declares, state pgAdmin's port.
- **YAML anchors do not cross compose files.** pgAdmin and Mailpit were defined only in the override
  file, so they never inherited `*default-hardening` and ran with Docker's default capability set.
  The override now defines its own anchor.
- **MinIO runs as root** and is the one documented exception: its image ships a root-owned `/data`.
  Development-only stand-in for Azure Blob Storage (AD-44).
- **Production applies migrations as a separate step**, not on start-up
  (`dotnet ef migrations script --idempotent` piped to psql). This exposed a real gap: `SuperAdminSeeder`
  lacked the pending-migrations guard `PlatformIdentitySeeder` had, so it threw
  `relation "platform.users" does not exist` instead of warning. Fixed.
- `Serilog:ConsoleFormat` (`json` | `text` | unset) added so one image serves both a person reading
  `docker compose logs` and a collector reading production.
- CI gained a `docker` job: builds the image, `up -d`, smoke-tests health + login, boots every
  profile, asserts the hardening with `docker inspect`, and proves the production overlay refuses to
  render without secrets.

### Still owed, in this order

- **~~Write the RLS startup validation (R14) with the first tenant-scoped entity.~~ Built and tested —
  see "R14 as built" below and `docs/BusinessRules/TenantIsolation.md`.** The startup validation that
  never existed now does, proven against a test-only table (301 integration tests green, all six
  isolation/resolution tests among them). **No production table is tenant-scoped yet.** ~~Two things
  were still open before Members could actually rely on it: the deployment connects to PostgreSQL as a
  superuser, which voids `FORCE ROW LEVEL SECURITY` entirely~~ — **closed by the database-roles
  close-out; see "Database roles (R14 close-out) as built" below.** One thing remains: no gym-scoped
  grant can be issued through the API yet, so `TenantResolutionMiddleware`'s grant check has never had a
  real grant to check against.
- **The Members domain is frozen** (`docs/Domain/MEMBERS-DOMAIN-DESIGN.md`). Implementation follows it
  exactly; a change needs a decision recorded in its §20 register first. Note the guardrails in its
  §19 — seventeen things an implementation must not quietly undo, chief among them: Membership Type and
  Membership Instance stay separate, Member Numbers are never derived or reused, no age is hard-coded,
  and no medical detail is stored anywhere.
- **Before a live gym uses it:** populate the legal values (§20.1–20.3, §20.6) from counsel, settle
  merge policy (§20.9), and tell the gym owner in writing about the offline-gateway limitation (§11.10).

## AD-39 idempotency framework as built

One migration: `AddIdempotencyRecords` (`platform.idempotency_records`). No production consumer yet —
the first is invoices mark-paid (M3.3, next increment); `IdempotentCommandConventionTests` keeps the
theory that enforces this live against an in-test-assembly exemplar until then.

1. **Claim commits immediately; the handler runs inside its own transaction.** `TryClaimAsync`'s
   INSERT autocommits — no ambient transaction — so every API instance sees the claim the moment it
   lands. `IdempotencyBehavior` then opens an explicit transaction around the handler and flips the
   record to `Completed`/`FailedDeterministic` inside it, so the business effect and the stored
   outcome commit atomically. Only idempotent commands (`IIdempotentCommand`) pay for any of
   this — every other request passes straight through the behavior.
2. **What is stored is the outcome, not an HTTP response.** A replay re-enters `WriteResults` with
   the decoded `Result`, exactly as a fresh execution would, so the two paths cannot drift in how a
   status or body gets built — the framework never has to know about HTTP at all.
3. **`ux_idempotency_records_claim` is raw SQL in the migration**, the same treatment M3.1 gave the
   `(plan, cycle, currency)` price index: EF's index builder cannot express this composite
   uniqueness either.
4. **`TryClaimAsync` bounds its retry at three attempts.** The INSERT can lose to a concurrent claim
   whose owner then releases it (a transient failure) before the losing call's fallback lookup
   runs — the row is simply gone, not "someone else holds it" — so the call loops back and retries
   the INSERT instead of surfacing a false conflict. Past three attempts that stops looking like the
   race and starts looking like a bug, so it throws.
5. **`IdempotencyRecordMaintainer`** purges expired records at startup — an interim owner of
   retention, same shape as `AuditLogPartitionMaintainer` (M1.3). Both were folded into M3.4's
   `RetentionJob`; the startup calls stay for first boot.

## Whole-repository audit (2026-08-11) — 10 findings, all fixed

The first adversarial review of the **older, never-reviewed increments** (M1 identity/auth, M2 gyms,
M3.1/M3.2, the audit interceptor, rate limiting) plus cross-cutting security, transactions, and
structure. 39 agents over 382 files; 38 candidates verified, 1 refuted; 27 lower-severity findings
(mostly duplication) below the reporting cap.

**Structure verdict: conformant.** No layering violations, no misplaced responsibilities, no drift
from the ARCHITECTURE.md folder plan — the architecture tests are holding. The duplication the
cleanup finders flagged (the `*Writes` helpers, sort scaffolding, session-termination loops) is
maintenance drag, not rot; the one place it had already caused divergence was the LIKE escaping,
now shared.

Fixed, each with a pinning test:

- **Refresh-token rotation is serialized** (`FindByHashForUpdateAsync` + a transaction). It was a
  read-then-write with no lock, so two simultaneous uses of one token both succeeded and reuse
  detection never fired — a replayed stolen cookie yielded a second live session invisibly. This is
  the guarantee `ConcurrencyModelTests` cites to justify exempting `RefreshToken` from concurrency,
  and it was not true until now.
- **Change-password is no longer an unthrottled, untraced oracle**: lockout is checked before the
  current password is verified, wrong attempts count towards the same per-account lockout as
  sign-in and are audited (`auth.password_change_failed`), and the endpoint carries the sign-in rate
  limiter. A successful guess ends every session, so this door needed sign-in's guards.
- **Deleting a gym cancels its live subscription in the same transaction**, and the renewal sweep
  excludes deleted gyms defensively. Assignment already refused a deleted gym; nothing enforced the
  same intent on the delete path, so the M3.4 sweeps billed deleted tenants every cycle, forever.
- **The persistence boundary translates unique-index violations** into
  `UniquenessConflictException` → 409 `persistence.uniqueness_conflict` (with the constraint name).
  Slug, normalized-email, custom-domain and one-live-subscription races previously reached callers
  as bare 500s — the handlers' pre-checks are a courtesy and the index is the guarantee, so the
  race is expected, not exceptional.
- **The audit trail fits values to their columns.** An over-long email in a failed sign-in made the
  audit write throw (22001), turning a 401 into a 500 and losing the row documenting the attempt.
  An audit entry must never be why a request fails.
- **The last-SuperAdmin invariant is serialized** by a new `IAdvisoryLock`
  (`pg_advisory_xact_lock`), held from the count to the commit. It constrains a *set*, so two
  administrators editing two *different* users both passed the check and both committed — no
  row-level token can see that.
- **Plan price upsert and removal require `If-Match`** (they overwrite what the caller read, and
  `SetPrice` is an upsert with no state machine to refuse a stale interleaving).
- **One LIKE-escaping implementation** (`SearchPatterns`) used by all five query classes: four of
  them passed the caller's `%`/`_` straight into ILIKE, so a search for "100% Fitness" returned
  every row starting with "100" and a bare `_` returned the whole table.
- **The retention job purges refresh tokens** — the purge `IRefreshTokenRepository` documented and
  nothing performed. Rotation inserts a row per refresh, and the session-ending paths load every
  token a user ever held, so suspension got slower forever.

**Still open, deliberately:** concurrent failed sign-ins for one account still collide on the user
row's token (finding 7's 409 on `/auth/login`). The clean fix is a counter update that does not ride
the aggregate's version, which is a small domain change worth doing on its own rather than inside a
ten-finding batch.

## M3.4 as built

**Merged to `main`** (`bd722bb`, review complete). Design (approved before implementation):
`docs/superpowers/specs/2026-08-10-job-runner-design.md` — hand-rolled schedules over a cron
dependency, auto-PastDue on an overdue current-period invoice, trials auto-convert-and-bill.

- **The job runner is BuildingBlocks machinery**: `IScheduledJob` + `JobSchedule`
  (`Every`/`DailyAtUtc`, deliberately not cron — Hangfire is banned pending an ADR, and no job
  needs more) + `JobHost` (`BackgroundService`, one loop per job, fresh scope per tick, failures
  logged and the schedule advances). Cross-instance exclusion is `IJobLock` →
  `PostgresJobLock` (session advisory lock on the scope's own connection; a crashed holder's lock
  dies with its session — integration-tested both ways). `Jobs:Enabled=false` turns the host off
  for hand-driven tests. New package: `Microsoft.Extensions.Hosting.Abstractions` (approved
  `Microsoft.Extensions.*` family — no dependency-policy change needed).
- **`RetentionJob` (daily 03:10 UTC)** — the standing instruction executed: the recurring halves
  of `AuditLogPartitionMaintainer` + `IdempotencyRecordMaintainer` on a real schedule; the startup
  calls stay for first boot. Partition *detachment* still waits on the counsel retention window.
- **`RenewalJob` (15 min)** — candidates from the `(status, current_period_end_utc)` index, then
  one `SubscriptionRenewalService` step per subscription **in its own scope/transaction**. The
  four questions in order: expired trial converts-and-bills (unless cancellation is scheduled —
  converting would bill a customer on their way out); scheduled cancellation finalizes at period
  end (`CancelNow`, reason kept); auto-renew off expires (`Expired`, not `Canceled` — churn counts
  decisions); otherwise apply a due pending change, `Renew()` from where the period *ended* (no
  billing-date drift), and issue the renewal invoice at the **copied** price (the snapshot rule).
  PastDue renews too — service is never withheld by billing state. Every branch consumes the
  condition that selected it, so re-running a crashed sweep is a no-op (tested).
- **`DisableAutoRenew()`** added to the aggregate: §2.3's `auto_renew` column and the expiry
  branch are one policy, and nothing could turn the flag off short of cancelling. No endpoint yet
  (AD-45's lever); no event, the `CancelPendingPlanChange` reasoning.
- **`InvoiceOverdueJob` (hourly)** — `Issued` past due → `MarkOverdue(now)` (aggregate-guarded
  against a wrong-clock run; raises `invoice.became_overdue`, the future dunning hook) and, for a
  **current-period** invoice on an **Active** subscription, `MarkPastDue()` in the same
  transaction. Old invoices mark only themselves. Transitions added deliberately:
  `Issued → Overdue`, `Overdue → Paid | Void`; an overdue invoice is *open*, so mark-paid and void
  accept it (`IsOpen`), and the M3.3 settled-period rule closes the loop with no operator: pay
  everything open in the period and the subscription comes back.
- **The read models' one-definition overdue block** now includes the stored status: filter,
  projections, and the domain's `IsOverdue` all answer `Overdue ∨ (Issued ∧ past due)` — an Issued
  invoice is honestly overdue in the window before the sweep's next pass.
- The past-tense event checker learned `became` (`invoice.became_overdue`).

### Post-plan review fixes (workflow review, 10 findings, all fixed)

The same adversarial review gate M3.3 went through, before merge. All ten confirmed findings are
fixed with a pinning test each:

- **A running trial is never renewed or billed**, however far it outlasts its first billing
  period; the period chain catches up at conversion (`CatchUpPeriods`) so the first real invoice
  covers the period the gym is in — was: billed mid-free-trial, then billed again at conversion.
- **`period_anchor_utc` (migration `AddPeriodAnchor`)**: renewals restore the anchor's
  day-of-month whenever the target month has room, so a Jan-31 gym is a month-end gym forever —
  was: `AddMonths` chaining clamped it to the 28th for life. A cycle change re-anchors at the
  boundary it takes effect.
- **The sweeps decide with the database's clock** (`IDatabaseClock` → `PostgresDatabaseClock`,
  `SELECT now()`): durable and terminal verdicts — Overdue, Expired, a billed renewal — no longer
  trust any single host's clock. The idempotency store's reasoning, generalized.
- **The renewal step is one explicit transaction over a `FOR UPDATE` read**
  (`IUnitOfWork.BeginTransactionAsync` + `FindByIdForUpdateAsync`): a console edit racing the
  sweep waits its turn instead of winning at save time, which is what stopped every lost race
  burning a non-refundable invoice number. (EF cannot `FromSql` an entity with complex
  properties — the lock is a scalar statement, the load is the normal pipeline on the same
  transaction.)
- **Voiding re-runs the settled-period rule** (`PastDueRecovery`, shared with mark-paid): voiding
  the invoice that flipped a subscription PastDue no longer strands it flagged delinquent.
- **JobHost**: a job that cannot be constructed dies *loudly* (error log naming the type) without
  taking other loops down — was: an unobserved task fault and a green host; the cadence anchors
  to the schedule rather than to run completions (no per-run drift, no catch-up storms);
  `AddScheduledJob` is idempotent like every other registration.
- **One overdue boundary**: every binding — sweep candidates, read-model filter and projections,
  and the domain's `Invoice.IsOverdue` — is now inclusive (`due <= now`).

## M3.3 as built

**Merged to `main`** (`e39eb73`, review complete). All seven tasks done, each its own
commit. Design: `docs/superpowers/specs/2026-08-08-invoices-design.md`. Plan:
`docs/superpowers/plans/2026-08-08-invoices.md`. Business rules as built:
**`docs/BusinessRules/Invoices.md`**.

| Task | State |
|---|---|
| 1 — Invoice domain (aggregate, transition table, rules, events) | ✅ `dbae5dc` |
| 2 — proration calculator + scheduled plan change | ✅ `3936aba` |
| 3 — persistence (EF config, `AddInvoices` migration, repository, number generator) | ✅ `615fdd1` |
| 4 — issuing on assignment / trial conversion / plan change, net-sign policy | ✅ `1f46070` |
| 5 — mark-paid (first production `IIdempotentCommand`), void, queries | ✅ `9ab1436` |
| 6 — infrastructure queries + endpoints + OpenAPI snapshot (5 new operations only) | ✅ `f7ae208` |
| 7 — docs, exemplar cleanup, full verification, live idempotent-replay proof | ✅ (this commit) |

**Test counts after the review fixes** — every project run complete, no `Aborted`, 0 build warnings:
SharedKernel 126 · BuildingBlocks 42 · Architecture **133** · Domain **326** · Application **181** ·
Infrastructure **67** · Integration **174** = **1049** (was 997 after task 2). Architecture went down
by two versus task 2: removing the two seeded idempotency exemplars deletes four theory rows while
the real `MarkInvoicePaidCommand` adds two.

**Unrelated latent failure fixed in passing:** `IdempotencyPipelineTests` pinned a `FakeClock` at
2026-08-07 while the store compares expiry against PostgreSQL's real `now()`; with 72h retention,
every stored outcome was born expired once real time passed 2026-08-10 12:00 UTC and all four tests
failed on any checkout. They now use `SystemClock`, like `IdempotencyStoreTests` always did.

### Decisions taken in tasks 3–7

- **The number function guards its own creation race.** Two first-of-the-year issuers can both pass
  the `pg_sequences` existence check and race `CREATE SEQUENCE`; `IF NOT EXISTS` does not close that
  window (PostgreSQL can surface it as a catalog unique violation). The function catches
  `duplicate_object`/`unique_violation` and proceeds — the loser uses the winner's sequence. The
  persistence test runs its 8 parallel callers **cold** so the guard itself is exercised.
- **"Same plan" is keyed on plan *and terms*, not plan id alone** — plan-id-only keying would forbid
  a same-plan cycle change (monthly → yearly), the adjustment
  `AGymAlreadyOnAWithdrawnPlan_CanChangeItsTerms` protects. **Post-review:** re-sending the current
  terms is an idempotent **no-op success** (the route is a PUT; retry-after-timeout must succeed),
  not the `change_to_same_plan` refusal originally built — that error is gone. With a pending change
  it is still the cancellation gesture.
- **`InvoiceIssuer` only Adds; the calling handler saves** — invoice and subscription change commit in
  one transaction (`SuperAdminGuard` precedent). `SubscriptionStateCommandHandlers.MutateAsync` gained
  an async-mutate overload so trial conversion can load the plan and issue inside the same flow.
- **Invoice line display order is a projection decision**: `invoice_lines` deliberately has no
  insertion-order column (lines are write-once), so the detail orders charges before credits
  (amount descending). The plan's "ordered by creation" was unimplementable without a schema addition
  nobody needs.
- **`InvoiceLine` is exempt from the concurrency-token rule**: write-once at issue, and the only
  mutations that exist (mark-paid, void) touch the invoice row, whose `xmin` guards them.
- **PastDue interplay is scoped to the current period**: paying an invoice issued on or after
  `CurrentPeriodStartUtc` flips a PastDue subscription back to Active in the same save; paying an
  older invoice does not.
- **The gym invoice tab returns an empty list for an unknown gym**, not 404 — "no invoices" is the
  truthful answer either way, and gym existence is the gym endpoints' question.
- **OpenAPI snapshot diff verified semantically**: exactly five operations added (`ListInvoices`,
  `GetInvoice`, `MarkInvoicePaid`, `VoidInvoice`, `ListGymInvoices`), none removed — the raw diff is
  large only because path insertion re-orders the document.
- The AD-39 HTTP tests deferred from that increment landed in `InvoiceApiTests.MarkPaidIsIdempotentEndToEnd`:
  missing key → 400 `idempotency.key_required`; replay → byte-identical body + `Idempotency-Replayed: true`;
  reused key with different body → 422 `idempotency.key_reuse_mismatch`; deterministic failure
  (`invoices.already_paid`) stored and replayed under its own key.

### Post-plan review fixes (workflow review, 10 confirmed findings, all fixed)

A high-effort adversarial review before merge confirmed ten findings — six of them real money bugs
concentrated in the plan-change path. All are fixed with a pinning test each:

- **Plan changes are gated by state before proration runs** (the decision tree in
  `docs/BusinessRules/Invoices.md`): Trialing → change now, bill nothing (was: prorated a period the
  gym never paid for, then double-billed at conversion); period already ended → change now, bill
  nothing (was: parked on a pending change dated in the past that no job applies); PastDue →
  scheduled at period end (was: credited unpaid time); cross-cycle → scheduled at period end (was:
  allocated e.g. an annual price over a monthly period, a ~12× overcharge).
- **`GymSubscription.ChangePlan` clears `PendingChange`** — an applied change supersedes a scheduled
  one, or the renewal job would revert a paid upgrade.
- **Same-terms re-assign is an idempotent no-op success**; `subscriptions.change_to_same_plan`
  removed (PUT retry safety, restoring main's behavior).
- **PastDue recovery requires the period to be *settled***: mark-paid flips the subscription only
  when no other open invoice of the current period remains
  (`IInvoiceRepository.AnyOtherOpenForSubscriptionAsync` — `InvoiceStatusTransitions.IsOpen`'s first
  consumer).
- **`ListForGymAsync` pages until complete** (was: silently truncated at 200), **invoice search
  escapes LIKE metacharacters**, **the status filter validates against enum names** (Enum.TryParse
  accepts any integer string), and the **overdue predicate has one definition** in `InvoiceQueries`.

### Decisions taken in tasks 1–2

- **Tax rounds to the currency's minor units**, via `Subtotal.Multiply(rate, RoundingPolicy.HalfEven)` —
  not the plan's `Math.Round(…, 2, …)`, which truncates KWD (3 minor units) and inflates JPY (0). Same
  answer at today's 0% rate; both currencies are pinned by tests. **Task 3's `tax_rate numeric(5,4)` column
  is unaffected**, but the amount columns must keep `MoneyConventions`' `numeric(14,4)`.
- **`Draft` and `Overdue` are declared but unreachable in both directions**, asserted by the 5×5 matrix.
  Draft waits on manual invoicing, Overdue on the M3.4 job; until then overdue is derived
  (`Invoice.IsOverdue`), the M3.2 trial-expiry precedent. Whoever gives either a producer adds its
  transitions deliberately. **M3.4 did exactly that for Overdue** (`InvoiceOverdueJob` →
  `MarkOverdue`, plus `Issued → Overdue` and `Overdue → Paid | Void`); Draft is still unreachable.
- **A negative invoice total throws.** The design routes a net credit to a scheduled plan change, so a
  negative total is always a caller defect — there is no credit-note document to carry one.
- **Line quantity must be 1**, refused with a message naming the constraint. Forces the first producer of
  quantity > 1 to add the multiply-with-a-rounding-policy path rather than inherit `Amount = UnitPrice`.
- **`Proration.Compute` takes both day counts from one calculation**, which is what makes the net's sign
  stable — and the sign is the entire upgrade/downgrade policy. Returns null when no days remain; clamps
  `daysUsed` so a backdated request cannot produce a negative ratio (which `Allocate` refuses).
- **The event-name worry written into task 2 was unfounded.** `EventNamesUseThePastTense` requires *any*
  underscore-separated word to be past tense, not every one, and `cancellation_scheduled` was already the
  precedent — so `gymos.platform.subscription.plan_change_scheduled` passes unchanged.
- **`CancelPendingPlanChange` raises no event**, unlike `WithdrawCancellation`: nothing acts on a scheduled
  change before the renewal job applies it, so there is no consumer to inform.
- **`PendingPlanChange` is exempt from the concurrency-token rule** — EF reports owned types from
  `GetEntityTypes()`, and it lives in the subscription's row, which its `xmin` already guards.
- **No FK on `pending_plan_id` / `pending_plan_price_id`** — EF cannot express one from inside an owned
  type without raw SQL. Low risk while plans are undeletable (M3.1 makes `DELETE` a 405); revisit if that
  ever changes.
- Extra domain guards added beyond the plan, each with a test: empty gym/subscription id, blank invoice
  number, due-before-issued, negative tax rate, blank or over-long line description
  (`InvoiceLine.MaximumDescriptionLength = 500` — **task 3's EF config should reuse that constant**).
- Test files are **flat** in `tests/GymOS.Platform.Domain.UnitTests/`, matching that project; the plan's
  `Invoices/` and `Subscriptions/` subfolders do not exist and should not be created.

### Environment notes from this session

- `dotnet ef migrations add` worked with the dev stack already up (`gymos-postgres` + `gymos-api`
  containers healthy); no host `dotnet run` was needed and nothing locked the build output.
- **The test host did not crash once** across ~10 full-project runs, including the 155-test integration
  suite. The `0x80131506` warning below still stands, but it did not reproduce today.
- **PowerShell here-strings (`@'…'@`) break `git commit -m` when the message contains double quotes** —
  the argument terminates early and git reads the rest as pathspecs. Write the message to a file and use
  `git commit -F` for anything with quotes in it.

## API reference (OpenAPI + Scalar) as built

No migration, no domain change — host and contract only. New package: `Scalar.AspNetCore` (MIT,
host-only; approved in `docs/Engineering/DEPENDENCY_POLICY.md` in the same commit as the
`ForbiddenPackagesTests` entry, per policy rule 5).

1. **The document now declares authentication.** `components.securitySchemes.bearerAuth` (http /
   bearer / JWT) plus a per-operation `security` requirement. It previously documented 25 separate
   `401` responses while never saying how to avoid one — generated clients emitted no auth handling
   and a UI had nothing to bind an authorize control to. The requirement is derived from each
   endpoint's own `IAuthorizeData`/`IAllowAnonymous` metadata rather than a hand-kept list, so the
   document cannot drift from the pipeline. **43 of 45 operations are secured; the two that are not
   are `POST /auth/login` and `POST /auth/refresh`** — login issues the token, and refresh reads a
   rotating token from an httpOnly cookie, not the bearer header. Per-operation rather than a root
   `security` block for exactly that reason.
2. **Publication is opt-in outside Development** (`OpenApi:Enabled`). `MapOpenApi()` was previously
   ungated and unauthenticated in *every* environment, publishing a complete map of endpoints,
   parameters and schemas to anonymous callers. The default is derived from the environment **in
   code**, not from `appsettings.Development.json`, so a host that loads no settings file — the
   integration-test host — still behaves like a developer machine. Same opt-in shape as
   `Security:ForwardedHeaders`.
3. **Scalar API reference at `/scalar/v1`**, gated by the same flag: a UI that loads but cannot
   fetch its document is worse than no UI, so the two are mapped and asserted as a pair.
4. **The browser opens on startup** — `ApiReferenceBrowserLauncher`, an `IHostedService` that opens
   `/scalar/v1` once the server is listening.

### Decisions and traps found

- **`launchSettings.json`'s `launchBrowser` does not work under `dotnet run`** — it is honoured by
  Visual Studio, Rider and `dotnet watch` only. Verified, not assumed: with `launchBrowser: true`,
  `dotnet run` opened nothing (browser process count unchanged). The launcher therefore lives in the
  application, which covers every launcher with one mechanism, and `launchBrowser` stays **false**
  so the two cannot open two tabs. Confirmed working: Chrome opened in the same second the host
  logged `Now listening`.
- **A server process that opens a browser must be impossible to get by accident.** It is registered
  only when the reference is published, the environment is Development, and
  `DOTNET_RUNNING_IN_CONTAINER` is not set — a containerised host has no display. `OpenApi:LaunchBrowser`
  overrides either way. The decision matrix is pinned by tests, because the registration is the part
  a later edit could quietly widen.
- **It does not fire under `WebApplicationFactory`** — `TestServer` binds no address, so the launcher
  no-ops. Verified by running a test and counting browser processes (delta 0). That is a property of
  the test host rather than of this code, which is why the registration tests exist.
- **The OpenAPI snapshot was regenerated deliberately** and the diff read semantically: 45 operations
  before and after, 43 schemas before and after, **zero** added or removed — the only change is
  security metadata.
- **A new integration suite must take an isolated database — this one did not, and the shared suite
  went flaky.** The first version of `OpenApiExposureTests` booted seven hosts against
  `PostgresFixture.ConnectionString`, and because `Jobs:Enabled` defaults to **true**, each of them
  started the renewal and overdue sweeps against the database other suites were asserting on. One
  full run failed; the next passed. Fixed with `CreateIsolatedDatabaseAsync` plus
  `Jobs:Enabled=false` and `Database:MigrateOnStartup=false` — the suite routes HTTP and reads no
  data, so it needs neither. The integration suite also got **40 seconds faster**. Restates the M1.3
  lesson with a second mechanism: it is not only assertions on row counts that leak between suites,
  it is background jobs.
- **Test counts after this work: 1116** (was 1101) — Integration 179 → 194.
- `Scalar.AspNetCore` **reads** the document and never generates it, so `Microsoft.AspNetCore.OpenApi`
  remains the single source of the contract and the M1.3 snapshot gate is untouched. Commercial-pivot
  watch applies (Scalar sells a hosted product beside the MIT library); it is confined to the
  presentation edge and nothing depends on its types, so replacing it is deleting two calls.

## Enforced-header declarations as built

**Merged to `main`** (`5ee9aef`). No migration, no domain change — host and contract only. Built as the
prerequisite for the frontend's F2 billing increment, which was the first thing to consume this
contract the way a generated client does.

**The defect.** Three headers were enforced by the pipeline and documented in prose alone:
`Idempotency-Key` by `IdempotencyHeaderMiddleware` (AD-39), `If-Match` by `PreconditionHeaders.IfMatch`
inside the handlers, and `X-Confirm-Action` by a comparison inside the command (AD-6). None was a
declared parameter, so `openapi-typescript` could not surface any of them and every new client
rediscovered the requirement by getting a 4xx. `POST /invoices/{id}/mark-paid` alone needs two of the
three. This was the **third** instance of one underlying fault — the document not describing what the
pipeline requires; the first two were the missing bearer scheme and the empty `security: [{}]`.

1. **Declared from markers on the endpoints that enforce them** (`RequiredHeaders.cs`): `RequiresIfMatch()`,
   `HonoursIfMatch()`, `RequiresIdempotencyKey()`, `RequiresConfirmAction()`, read by the same operation
   transformer that adds the bearer requirement. The declaration sits beside the route it describes, and
   the R13 snapshot gate turns any omission into a reviewed diff rather than a silent one.
2. **`If-Match` requiredness is uneven, deliberately, and the contract now says so.** `versionRequired`
   in the command is the source of truth, and only **five** pass `true` — `UpdatePlan`, `UpdateGym`,
   `UpdateGymBranding`, `UpdateUser`, `SetUserRoles`. Every one is a **PUT that overwrites fields the
   caller did not send**, where a missing precondition is last-write-wins over data nobody saw. Action
   endpoints — plan activate/deactivate and the price writes, gym lifecycle, user suspend/reactivate,
   the subscription writes, and invoice `mark-paid`/`void` — pass `false`: they honour the header when
   supplied (a stale value is still 409) but do not demand it.
3. **`Declare` de-duplicates by header name**, so chaining two contradictory markers on one route cannot
   emit duplicate parameters — which would break `openapi-typescript` downstream.

### Decisions and traps found

- **Reading a header is not requiring it, and the difference was nearly inverted into the contract.**
  The implementing agent stopped before writing code to challenge an instruction that said every
  `If-Match` call site was required. It was right: following it would have declared the header mandatory
  on about twelve endpoints where it is optional — the same lie this work removes, pointing the other
  way. A contract that overstates enforcement is as false as one that hides it, and a generated client
  would have been forced to supply a value it never needed.
- **A test that samples cannot pin what the snapshot already pins.** The theory covers 7 of the 25
  declared headers; `OpenApiContractTests` byte-pins the whole document, so flipping any declaration
  fails CI regardless. What is still missing — recorded as owed — is a **semantic** assertion binding
  requiredness to `versionRequired`, so the two cannot drift apart silently.
- **Test counts after this work: 1123** (was 1116) — Integration 194 → 201.

## M4 as built

Design approved 2026-08-11 — `docs/superpowers/specs/2026-08-11-feature-flags-design.md`. Business
rules as built: **`docs/BusinessRules/Features.md`**. Branch
`m4-feature-flags`, merged to `main` after each task. **Nothing consumes a resolved flag yet**, and
that is deliberate (design §6): the tenant-facing modules that would — POS, inventory, CRM — do not
exist. M4 delivers the decision and its API; the first consumer enforces it.

**The rule, in one line:** `override ?? plan ?? platform default`, with the layer that decided it
reported alongside the answer. `FeatureResolution` is the single definition; the write path and the
per-gym read call it directly. **M4.1 correction:** this section originally went on to claim
resolution is "deliberately not done in SQL" at all — false, and one of the three places the M4.1
review found that exact claim. `GET /features`' `AdoptionCount` restates the rule as a translated LINQ
expression, because counting adoption per gym in C# would mean loading every gym; it is correct, and
now pinned against `FeatureResolution.Resolve` by `FeatureAdoptionParityTests`. See "M4.1 as built"
below.

1. **Domain** (`eb185b3`) — `Feature` (immutable key, editable copy/default/premium),
   `GymFeatureOverride` (required reason, one row per (gym, feature)), `PlanFeature` (the tri-state:
   include / exclude / **say nothing**, where the third is not the second), `FeatureResolution`, and
   `PlatformFeatures`' twelve declared keys with reasoned defaults — operational core on and free,
   sold capabilities off and premium, per-use or hardware-dependent off.
2. **Persistence** (`d14f601`) — three tables, both unique indexes, and an **insert-only**
   `FeatureCatalogSeeder`. Unlike the permission seeder it does **not** reconcile: `name`,
   `description`, `category`, `default_enabled`, and `is_premium` are operator-owned (§3.5), so
   overwriting them on restart would silently undo a console edit. Orphaned keys are logged, never
   deleted — a delete would cascade into plan contents and overrides.
3. **Catalogue endpoints** (`8cc97ea`) — `GET /features` with a real **adoption count** (gyms
   resolving it *on* through all three layers, not a count of plan rows), `PUT /features/{id}` with
   `If-Match` **required**: changing `defaultEnabled` re-answers the feature for every gym that has
   neither an override nor a plan opinion, the widest blast radius of any write in the module.
4. **Plan contents** (`c51d9bb`) — `GET/PUT /plans/{id}/features`, gated **`billing.manage`**, not
   the feature permission: what a plan includes is priced. Only the keys sent are touched;
   `MarkAggregateChanged` moves the plan's own version so concurrent feature edits collide.
5. **Per-gym overrides** (this task) — `GET/PUT /gyms/{id}/features` gated
   `features.read`/`features.manage`, plus the `features` block on `GET /gyms/{id}` that §3.3 always
   described. A `PUT` stores an override only while it differs from what the gym would get anyway, so
   asking for exactly what the plan (or default) already gives **withdraws** the exception rather
   than restating it — a gym that later changes plan then follows the new plan instead of staying
   frozen by a row nobody remembers creating. `reason` is required only when an override is genuinely
   needed, and the response is the **full resolution, not an echo**: after a withdrawal the stored
   state is deliberately not what was sent. **M4.1 correction:** this held only at the instant of that
   one write — nothing re-checked it afterward, which is the Critical the M4.1 review found. The
   actual guarantee, since M4.1: a redundant exception is withdrawn **immediately** when the change is
   to that gym's own subscription, and **within a day** when the change is to a plan or a platform
   default, via the nightly `feature-override-sweep`. See "M4.1 as built" below.
6. **Close-out** (this commit) — `docs/BusinessRules/Features.md`, full verification, and the live
   run the design's §7 asks for.

### Verified live (task 6)

Against the compose stack (`docker compose up -d`, rebuilt image, real PostgreSQL), the design's §7
scenario end to end:

- **The same feature, three answers, three reasons.** One plan flipped to include `crm`; a gym on
  that plan reads `enabled=true source=Plan`, a gym with an exception reads
  `enabled=true source=Override` carrying its reason, and a gym with no subscription reads
  `enabled=false source=Default`. Only `source` says which lever changes each.
- **The self-cleaning rule, live.** Asking the plan gym for exactly what its plan already gives
  stored nothing and needed no reason; asking the override gym for the default **withdrew** its
  exception (`overrideCount` back to 0) rather than storing a redundant row.
- **Validation ahead of mutation.** A reasonless exception (`features.override_reason_required`), an
  unknown key (`features.unknown_key`), a key stated twice (`features.duplicate_key`) and a **mixed**
  request whose second key lacked a reason were each refused whole — 0 overrides stored by any of
  them. `PUT /features/{id}` without `If-Match` → 428 `concurrency.version_required`;
  `POST /features` → 405.
- **The seeder really is insert-only.** `crm`'s platform default was flipped to `true` and its name
  edited through the API, the API container restarted, and the edit **survived** — 12 rows still, no
  duplicates. This is the claim with the nastiest failure mode (an operator's change works until
  someone reboots), so it is worth proving outside the test suite too.
- The gym detail block returns the same 12 resolved rows as `GET /gyms/{id}/features`, and
  `adoptionCount` moved with the resolution rather than with plan rows.

**Test counts, every project run to completion with no `Aborted`, 0 build warnings:** SharedKernel
126 · BuildingBlocks 62 · Architecture 151 · Domain 391 · Application 232 · Infrastructure 67 ·
Integration 233 = **1262** *(as at task 6; the review below adds five more)*. (The 1,028 unit figure
recorded after task 5 was one short; the measured unit total is 1,029.) The integration suite
completed first time on this run — none of the
intermittent connection-open failures noted after task 5.

### Decisions and traps found

- **`PlanRepository` loaded plans without their `Features` — a real defect that task 5's integration
  test caught, and that broke task 4 in production.** `SetFeature` inserted a second row for a key the
  plan already had and died on `ux_plan_features_plan_feature`, so `PUT /plans/{id}/features` could
  not be used twice on the same key; `ClearFeature` found nothing to remove; and the plan layer was
  invisible to the override write, which then stored exceptions the plan already granted. **Unit tests
  could not see it** — the fake repository always hands back the whole aggregate. `PlanApiTests` now
  edits the same plan feature three times over real PostgreSQL. The general lesson: an `Include` that
  is missing from a repository is invisible to every test that does not use the real one.
- **No `If-Match` on `PUT /gyms/{id}/features`**, unlike the plan and catalogue writes. There is no
  single version to match: each override is its own aggregate keyed on (gym, feature) and the request
  merges per key, so two operators editing different features do not conflict at all. Two editing the
  same one is a disagreement about policy rather than a lost update — the later decision wins, carries
  its own required reason, and the audit trail keeps the earlier one.
- **Validation runs entirely ahead of mutation.** An unknown key, a duplicate key, or a missing reason
  refuses the whole request: a caller who cannot tell which half of their intent landed is worse off
  than one who was refused. The reason check is duplicated in the handler rather than caught out of the
  aggregate, so a five-key request cannot write three before discovering the fourth is unjustified.
- **The same key twice is refused** (`features.duplicate_key`) rather than applied last-one-wins,
  which would report success for a self-contradicting request — and would otherwise surface as an
  unexplained 409 from the unique index.
- **Resolution is delegated, never re-implemented.** `GymQueries` calls `IFeatureQueries` for the
  gym-detail block, and `GetGymFeaturesQuery` proves the gym exists with a new
  `IGymQueries.ExistsAsync` rather than loading the whole detail projection — which now resolves the
  catalogue itself and would have done it twice per request.
- **Test counts after task 5: 1261** — unit 1,028, integration 233. The full integration suite shows
  intermittent connection-open failures under parallel load (2 of 233 on the run for this task, both
  green in isolation) — the same environmental symptom recorded for the M3.3 run, not an assertion
  failure.

### M4 review (2026-08-11) — 2 fixed, 2 recorded, 1 refuted

**Coverage caveat, stated because it matters:** this was a **single-pass review**, not the
multi-agent adversarial gate M3.3 and M3.4 went through. It should not be read as equivalent
assurance, and running the real gate over M4 is still worth doing.

**Structure verdict: conformant**, with one clause this single-pass review got wrong and the
2026-08-12 whole-increment review corrected (§5 of the M4.1 design): `GymQueries` delegates rather
than reprojecting; the permission split matches the design; both `SubscriptionPlan` loaders carry the
`Features` include whose absence caused the task-5 defect, and nothing else loads a plan. One thing
checked specifically because it would have failed silently: a cleared override raises its event on an
aggregate the repository then **removes**, which would be lost if the outbox interceptor filtered by
entity state. It does not — every tracked aggregate is drained regardless of state.

**The wrong clause, left visible rather than silently rewritten:** this review recorded "resolution
lives in the Domain as one pure function and is never re-expressed in SQL" as part of the conformant
verdict above. That is false — `FeatureQueries.Project()`'s `AdoptionCount` restates the rule as a
translated LINQ expression, correctly, and always did. This was one of the three places (alongside
`Features.md` and this file's M4 summary above) the 2026-08-12 whole-increment review found asserting
a structural property the code does not have; M4.1 fixed the documentation and added
`FeatureAdoptionParityTests` to pin the restatement against `FeatureResolution.Resolve`. Recorded here
as history of what this review actually concluded, not as a claim still believed — see "M4.1 as
built" below for what is true now.

Fixed, each with a pinning test:

- **`adoptionCount` and `overrideCount` counted different populations.** Adoption excluded deleted
  gyms; the override count counted every row, so "2 of 5 gyms hold an exception" could include dead
  tenants. Both now start from the same non-deleted gym population, written the same way so the
  shared filter is visible rather than remembered.
- **A no-op `PUT /plans/{id}/features` moved the plan's version and emitted an event.**
  `SetFeature` announced a change it had not made — unlike `Feature.SetDefault`, which already
  returned early — and the handler called `MarkAggregateChanged` for any non-empty request. The route
  is a PUT, so an identical retry is expected; charging it a version bump invalidated every other
  reader's `If-Match` over a write that did nothing. Both now report whether anything actually
  changed.

Recorded rather than fixed:

- **An override never expires** — the gap deserves its own increment and a product decision first;
  the full reasoning is in `docs/BusinessRules/Features.md`. Listed under "Known open items".
- **`GET /features` adoption is O(features × gyms)** — two correlated subqueries per gym per feature,
  unbounded. Deliberately left alone: the query's own comment records that an earlier, cleverer
  version was "correct-looking and unverifiable", and trading that back for performance nobody needs
  yet is the wrong direction. Listed under "Known open items" with the trigger to revisit.

**Refuted:** the suspicion that a stale `If-Match` slips through `PUT /features/{id}` when the body
changes nothing (no EF change → no UPDATE → the `xmin` precondition never tested). It does not —
409 `concurrency.conflict` either way, because setting the token's original value to a stale one is
itself a modification. Checked live rather than reasoned about, which is why it did not become a
"fix" for a bug that was not there.

## M4.1 as built

**Branch `m4-review-fixes`, seven commits (`613f517`..this commit).** Design (approved 2026-08-12):
`docs/superpowers/specs/2026-08-12-m4-review-fixes-design.md`. Business rules revised in place:
**`docs/BusinessRules/Features.md`**. This is M4's own whole-increment review, the gate M4 never
received before merging to `main` — one Critical, six Important, eight Minor, this increment fixes
the Critical and all six Importants and records the eight Minors as still-deliberate deferrals.

**The Critical.** M4's headline claim — "an override is stored only while it differs from what the
gym would get anyway" — was true for exactly one instant, the write that created it. Nothing
subscribed to a plan's feature opinion changing, a platform default flipping, or a subscription
ending, so a gym upgraded onto a plan that granted what it had been given by exception kept a
redundant row: `source: Override` reported forever for a capability it now paid for, and a later
downgrade kept a capability it no longer bought. Every earlier test moved the override while the
layers beneath stood still; not one changed a plan or a default *underneath* an existing override.

1. **The rule, re-evaluated rather than re-defined** (`613f517`) — `GymFeatureExceptions.Redundant`
   asks the *same* `FeatureResolution.RequiresOverride` the write path already called, applied to a
   gym's existing overrides instead of only the one being written. Pure, total, no database, the
   `FeatureResolution` shape, for the same reason.
2. **The synchronous half** (`af14364`, critical fix `9be8f17`, amendment `d1237a3`) — assigning a
   gym a plan, changing its plan, or ending its subscription withdraws the exceptions that change
   makes redundant **in the same transaction**, including `RenewalJob`'s expire-and-cancel steps, not
   only operator-driven writes. The critical-fix commit stopped the maintainer re-querying
   `ISubscriptionRepository` for the gym's live plan — inside the same uncommitted transaction that
   query returned the plan being *left*, not the one being joined, since a brand-new assignment is an
   unsaved `Add` and a cancellation has only mutated `Status` in memory — and instead has every caller
   pass the plan id its own in-memory state already knows will be live once the transaction commits.
   The amendment then restructured `SubscriptionRenewalService.ProcessAsync` to call the maintainer
   once, after its branch chain and before the shared save, so the fourth branch (a pending plan
   change applying at the renewal boundary) is covered by construction rather than by remembering a
   fourth call site.
3. **The nightly half** (`4f25ca5`) — `FeatureOverrideSweepJob`, daily at 03:50 UTC (after
   `RetentionJob`'s 03:10 and `PlatformStatsJob`'s 03:30, M3.4's runner, under its `IJobLock`),
   re-evaluates every gym holding at least one override — a small population by construction — for
   the two triggers too wide to do inline: a plan's feature opinion changing, and a platform default
   flipping. Built as "re-evaluate this gym's exceptions", not "delete redundant rows", because
   override expiry (still deferred) is one more predicate in the same pass.
4. **Contract-shaping changes** (`3af4716`, follow-up `59d4727`, correction `ace0060`) — `GET
   /gyms/{id}` now omits the `features` block instead of leaking it to any `gyms.read` holder;
   `ResolvedFeatureDetail` gained `overrideSetAtUtc`/`overrideSetByEmail`; `FeatureListItem` gained
   `isDeclared`. The gate moved twice before landing right: the follow-up centralized it in
   `IGymQueries.GetAsync` so one method covered the direct `GET` and every write's read-back at once,
   but that put the permission check itself inside Infrastructure; the correction kept the one choke
   point (`GymWrites.ReadBackAsync` plus `GetGymQueryHandler`) but moved the actual
   `IPermissionEvaluator` check back into Application (AD-3), with `GymQueries` reduced to taking
   `includeFeatures` as a plain parameter from its caller.
5. **The write-path and seeder fixes** (`f31de42`, review fix `bd9db62`, `556c69b`, follow-up
   `3e1d635`) — `PUT /gyms/{id}/features` retries once on the unique-index race so a repeated
   identical grant converges instead of 409ing; `SetGymFeaturesCommandHandler` now reads the plan
   `FOR UPDATE` in the same transaction it decides and responds in, closing the plan-edit race; `PUT
   /plans/{id}/features` refuses `{}`/`null` (400, not 500) and a repeated key (`features.duplicate_key`,
   not last-one-wins); `FeatureCatalogSeeder` and `PlatformIdentitySeeder` both wrap their seed in
   `IAdvisoryLock` so two instances booting against a fresh database no longer race
   `ux_features_key`/`ux_...` into a raw `DbUpdateException` that kills the host; `PUT
   /features/{id}`'s read-back stopped paying for the adoption-count scan (`ProjectWithoutCounts`,
   `null` not `0` for a write response); `ux_gym_subscriptions_one_live_per_gym`'s hard-coded
   `status IN (1,2,3)` is now pinned against `SubscriptionStatusTransitions.Live` by a test so the two
   cannot silently drift apart.
6. **Documentation, the SQL/domain parity test, verification and the live run** (this commit) —
   `FeatureAdoptionParityTests` pins `FeatureQueries.Project()`'s SQL `AdoptionCount` restatement
   against `FeatureResolution.Resolve` over a seven-gym state matrix (no subscription, a plan that
   includes, a plan that excludes, a plan that is silent, an override each way, a deleted gym), so the
   deferred expiry work's fourth `Resolve` input cannot silently go missing from the SQL copy.
   `Features.md` and this file no longer claim resolution is "never expressed in SQL" — it has one
   authoritative definition plus one deliberate, pinned SQL restatement — and the self-cleaning
   invariant is reworded to what is actually guaranteed: immediate withdrawal on the gym's own change,
   within a day otherwise. `Feature.Rename` (as the M4 design named it) shipped as
   `Feature.Describe(name, description, category)`; the deviation is now recorded rather than left for
   a reader to notice the two disagree.

### Verified live

Against the compose stack (`docker compose up -d --build`, rebuilt image, real PostgreSQL), signed in
as `admin@gymos.local` — the design's §7 scenario, the Critical finding demonstrated fixed rather than
asserted:

- **A plan that excludes, an override that compensates, a plan that already includes.** A gym was
  assigned `LIVE_EXCL` (`crm = false`), granted an override (`crm = true`, "Goodwill trial while on
  LIVE_EXCL."). `GET /gyms/{id}/features` read exactly what M4 always promised at that instant:
  `enabled: true, source: Override`, carrying the reason, the timestamp, and the author's email.
- **The exact scenario the design's §1 names.** The gym was then reassigned to `LIVE_INCL`, a plan
  that already includes the same feature (`crm = true`) — the "upgrades onto a plan that grants what
  the override granted" case. `GET /gyms/{id}/features` immediately after read `enabled: true,
  source: Plan`, with `overrideReason`, `overrideSetAtUtc` and `overrideSetByEmail` all `null` — the
  exception is gone, not merely outranked. `GET /features`' catalogue row confirmed it at the storage
  level, not just in the response: `crm`'s `overrideCount` read back **0**. This is the row that would
  otherwise have sat there for a year until a downgrade quietly resurrected a capability the gym no
  longer paid for — the M4 review's second, more expensive consequence — and it no longer exists.
- **`GET /gyms/{id}` for a SuperAdmin still carries the `features` block** — 12 entries, unchanged
  by the omission logic that now runs for a `features.read`-less caller.

Nothing about the run needed a retry, a workaround, or a second attempt — the sequence above is the
literal, unedited order of calls made against the freshly rebuilt image.

**Test counts, every project run individually to completion with no `Aborted`, 0 build warnings:**
SharedKernel 126 · BuildingBlocks 62 · Architecture 151 · Domain 423 · Application 235 ·
Infrastructure 67 · Integration 275 = **1339** *(1338 as at the seventh review-fix commit; the
whole-branch review below adds the eighth)*.

### Decisions and traps found

- **A read inside an uncommitted transaction can return the state a caller is in the middle of
  leaving, and the task-2 critical fix exists because of exactly that.** `FeatureExceptionMaintainer`
  queried the gym's live plan through the repository to decide what was now redundant, but the query
  ran inside the same transaction as the subscription change it was reacting to — an unsaved `Add` is
  invisible to it, and `CancelNow`/`Expire` had only changed `Status` in memory, so the query matched
  the row as it stood before either commit. The three lifecycle tests had passed anyway, by accident:
  EF's identity resolution happened to hand back an object a caller had already mutated earlier in
  the same method, a coincidence `.AsNoTracking()` would have silently broken. The fix removes the
  repository dependency entirely — every caller now passes the post-change plan id from its own
  in-memory state — and two regression tests were written and confirmed failing against the prior
  implementation before the fix landed.
- **Centralizing a gate and honouring AD-3 pulled in different directions, briefly.** Moving the
  `features.read` check into `IGymQueries.GetAsync` gave one method that covers the direct `GET` and
  every write's read-back by construction — but doing the permission check itself from there put an
  authorization decision in Infrastructure, a deviation from AD-3 that would need a superseding ADR,
  and a discoverability regression (a query method whose signature gives no sign it checks a
  permission). The fix keeps the single choke point (`GymWrites.ReadBackAsync`, plus
  `GetGymQueryHandler` for the direct read) but does the actual check in Application, passing the
  answer down as a plain `includeFeatures` parameter — both role-based tests passed unmodified, and
  the OpenAPI snapshot diff was empty, because the observable behaviour never changed.
- **A race test that pushes both writes toward the same value can never fail, protection or not — a
  review fix (`bd9db62`) caught this in the plan-edit-race test itself.** The first version had the
  gym ask for CRM `true` while the plan concurrently moved to `true`: every interleaving converges,
  because an override outranks the plan once created and the "no override needed" branch is only
  reached when the locked read already agrees with the plan's new value — so the test proved nothing
  about whether `FindByIdForUpdateAsync`'s lock actually did its job. Rewritten around a
  self-consistency invariant instead — the gym asks for the *opposite* of what the plan is racing
  towards, and the one assertion that matters is that the write's own response matches what was
  asked for, which can only fail if the decision used a plan value a concurrent write had already
  superseded. Confirmed failing against the prior implementation before the fix landed.
- **A lock held across the decision is not the same as a lock held across the response.** The same
  review fix found `SetGymFeaturesCommandHandler` releasing the plan's `FOR UPDATE` lock at
  `CommitAsync`, *before* the read-back that builds the response — a gap where a blocked concurrent
  plan write could land between commit and read-back, so the body returned could already be stale by
  the time the caller saw it. The read-back moved inside the transaction, before commit, so the
  response is always exactly the resolution that transaction is committing.

### Whole-branch review (2026-08-12) — 1 fixed, 1 recorded, 1 cheap fake fix

The gate before a human decides whether to merge `m4-review-fixes` — the same protocol as the "M4
review" above, run over the seven review-fix commits together rather than over M4 itself.

Fixed, with a regression test confirmed failing before the fix landed:

- **`DeleteGymCommandHandler` was the one `CancelNow`/`Expire`/`ChangePlan`/`Assign` site never wired
  to `IFeatureExceptionMaintainer`.** Deleting a gym cancels its live subscription — the plan layer
  goes silent exactly as an expiry does — but nothing re-evaluated the gym's exceptions afterward, so
  an override made redundant by the deletion survived until the nightly sweep. This was not merely a
  gap in behaviour: the design's §2 table, `Features.md`'s guarantee, and this file's own §5 summary
  all stated "assigned a plan, changes plan, **or its subscription ends**" is synchronous, and the
  code did not do that for one of the three ways a subscription ends. Fixed by adding the same
  `WithdrawRedundantAsync(command.GymId, null, cancellationToken)` call, before the same save, that
  the other three call sites already make — `null` because `CancelNow` has already set the
  in-memory subscription to non-live, the same reasoning `SubscriptionRenewalService` and
  `SubscriptionStateCommandHandlers` already document. `GymFeatureLifecycleTests
  .A_gym_deletion_withdraws_an_exception_it_has_made_redundant` failed with 1 override still present
  where 0 was expected before the fix, and passes now.

Recorded rather than fixed:

- **`AssignSubscriptionCommandHandler` decides feature redundancy against a plan read with no lock**,
  unlike the sibling `SetGymFeaturesCommandHandler` this same increment already closed the same race
  for. Narrow — only a `PUT /plans/{id}/features` racing a subscription assignment/change on the exact
  same plan can lose a capability — and a lost capability rather than corrupted data, traceable via
  `GymFeatureOverrideCleared` in the outbox. Full reasoning in `docs/BusinessRules/Features.md`; listed
  under "Known open items" below rather than fixed here, since the fix is the same `FindByIdForUpdateAsync`
  shape sized for the busiest handler in the subscriptions slice, not this wave.

Cheap, fixed:

- **`FakeFeatureQueries` shared one projection between `ListCatalogueAsync` and `GetAsync`**, hard-coding
  `AdoptionCount: 0` where production's `GetAsync` (backing `PUT /features/{id}`'s read-back) uses
  `ProjectWithoutCounts` and returns `null` for both counts. Inert today — no test asserted on
  `GetAsync`'s counts — but the same missing-`Include` shape as the `PlanRepository` trap M4 already
  paid for: a fake whose shape differs from the real thing teaches the wrong contract to whichever
  test relies on it next. The fake now mirrors the split.

## M5 as built

Design approved 2026-08-11 — `docs/superpowers/specs/2026-08-11-m5-dashboard-analytics-design.md`.
Business rules as built: **`docs/BusinessRules/Analytics.md`**. Branch `m5-dashboard-analytics`, all
six tasks committed to it (not yet merged to `main` — this increment now goes for review as a whole,
the same protocol M4 followed).

**Five decisions taken with the owner up front**, all recorded in the design and honoured exactly as
written: M5 ships only the stat columns that have a real source; MRR is reported per currency with no
conversion; MRR counts Active + PastDue, not trials; churn keeps cancellations and expiries apart; and
`analytics.read` is not granted to SupportAgent.

1. **The MRR calculator and the `Billing` status set** (`cdbee68`, review fix `c3e2b94`) —
   `MonthlyRecurringRevenue.Calculate`, a pure domain function over `(status, cycle, price)` triples,
   is the **one** definition of MRR; both the live dashboard and the nightly job call it, so the
   platform's headline number cannot have two implementations that quietly disagree (the M4 review's
   lesson, applied here before either caller existed). `SubscriptionStatusTransitions.Billing =
   [Active, PastDue]` sits beside the existing `Live` set — deliberately not derived from it at the
   call site — because "does the one-per-gym rule apply" and "does this contribute revenue" are
   different questions that happen to overlap.
2. **The daily read model** (`36f93ec`) — `DailyPlatformStats` (population + event counts) and its
   owned `PlatformMrr` child, one row per currency, migration `AddPlatformStats`. Neither table carries
   an `xmin`: one writer, serialised by `IJobLock`, rows written once and never updated.
3. **The nightly job** (`86a9960`, review fixes `1a67418`, `2e4186a`) — `PlatformStatsJob` (daily
   03:30 UTC, after `RetentionJob`'s 03:10) computes **yesterday** from `IDatabaseClock`, and
   `DailyStatsCompiler` inserts if the day is absent and skips if present — the write-once rule is a
   return value, not just a comment. **A real defect found by review, not by a test**: `new_gyms` was
   originally filtered to non-deleted gyms like the population counts, so a gym created and deleted on
   the same day vanished from every series with nothing to explain why. The owner ruled the *design
   sentence* ("deleted gyms are excluded from every count") was too broad, not the code that already
   read `canceled_gyms`/`expired_gyms` unfiltered — population counts answer "who is a tenant now" and
   exclude deleted gyms, event counts answer "what happened today" and do not. §4 of the design was
   corrected in place rather than left saying something half-true.
4. **`analytics.read` and the dashboard endpoints** (`fb4390d`, review fix `cdc4007`) —
   `GET /dashboard/summary`, `/health`, `/activity?limit=` (the last on `audit.read`, not
   `analytics.read`, since it is the audit log in a trimmed shape). **A real defect found by review**:
   `/health` first called `HealthCheckService` directly from the endpoint with no CQRS query/handler
   behind it — the only operation in the whole application with just one permission-enforcement layer
   instead of two. Fixed with an `IDashboardHealthCheck` seam in Application and an adapter living only
   in the host (the health-check packages resolve exclusively from the ASP.NET shared framework, which
   Application-layer projects cannot reference), restoring the belt-and-braces pattern every other
   query uses.
5. **The three analytics series** (`bff30be`) — `GET /analytics/revenue|gyms-growth|churn`, all gated
   on `analytics.read` with `from`/`to` both required and capped at 366 days
   (`analytics.range_invalid`, 400). Every series returns only the days actually recorded — no gap is
   ever zero-filled — pinned by a test seeding two non-adjacent days and asserting exactly two points
   come back.
6. **Close-out** (this task) — `docs/BusinessRules/Analytics.md`, full verification, and the live run
   below.

### Verified live (task 6)

Against the compose stack (`docker compose up -d --build`, rebuilt image, real PostgreSQL), signed in
as `admin@gymos.local`:

- **Two gyms, one EUR plan (`M5_STANDARD`, €100/month), both assigned.** `GET /dashboard/summary`
  read `totalGyms: 16, liveSubscriptions: 13, mrr: [746.56 EUR]` immediately — no job had ever run.
  (The dev database already held pre-existing gyms and subscriptions from earlier manual/test sessions
  on this machine; the run demonstrates deltas against that baseline, not a clean-slate count. All
  pre-existing and newly-created gyms were still `Provisioning` — nothing in this dev dataset had ever
  been `POST .../activate`d — so `activeGyms: 0` throughout this run is a fact about seed data, not an
  M5 defect.)
- **Triggering the compiler by hand.** `PlatformStatsJob.Schedule` was temporarily changed to
  `JobSchedule.Every(TimeSpan.FromSeconds(30))`, the API image rebuilt, and the log line
  `Platform stats recorded for 08/10/2026.` appeared within one tick. `GET /analytics/revenue` then
  returned exactly one point — `{"date":"2026-08-10","mrr":[{"amount":746.56,"currency":"EUR"}]}` —
  matching the dashboard's live MRR figure exactly, because both read the same calculator. The edit was
  reverted immediately afterward and the API rebuilt again before the next step; `git status --short`
  and `git diff` on `PlatformStatsJob.cs` were both empty at that point, proved in this task's report
  before any commit was made.
- **The live-versus-recorded split, demonstrated rather than asserted — the single most important
  thing this run proves.** A third gym was created and assigned the same EUR plan. `GET
  /dashboard/summary` moved immediately: `totalGyms: 16 → 17`, `liveSubscriptions: 13 → 14`,
  `mrr: 746.56 → 846.56 EUR` (exactly the new gym's €100). `GET /analytics/revenue` over the identical
  range returned the **same single point, unchanged**: still just 2026-08-10 at 746.56 EUR — the
  series does not know the third gym exists, because the job that would tell it has not run since.
- **MRR excludes trials, live.** A fourth gym was assigned a plan with a 14-day trial. `totalGyms`
  and `liveSubscriptions` both rose (Trialing is in the `Live` set); `mrr` stayed at exactly 846.56 EUR
  — a trialing subscription is live but contributes nothing to revenue, exactly as §4 of
  `Analytics.md` states.
- **A gap is a gap, never zero-filled.** `GET /analytics/gyms-growth?from=2026-08-01&to=2026-08-11`
  (11 calendar days) returned exactly **one** point, at 2026-08-10 — the other ten days have no
  recorded row and were not synthesized as zeros.
- **The refusal.** `GET /analytics/revenue?from=2025-01-01&to=2026-02-04` (over 366 days) returned
  `400` with `"code":"analytics.range_invalid"` and the message naming the 366-day ceiling.

**What did not work smoothly:** nothing failed outright, but the dev database's pre-existing state
(16 gyms already `Provisioning`, none ever `Active`) meant the "before" figures were not a clean zero —
the run had to be read as deltas rather than absolute counts. Recorded here rather than reset, per the
instruction to report what actually happened rather than a tidier version of it.

**Test counts, every project run to completion individually with no `Aborted`, 0 build warnings:**
SharedKernel 126 · BuildingBlocks 62 · Architecture 151 · Domain 413 · Application 232 ·
Infrastructure 67 · Integration 251 = **1302**.

### Decisions and traps found

- **One calculator, two callers, is what let the live-run's revenue figures agree without either being
  faked.** `DashboardQueries.GetSummaryAsync` and `DailyStatsCompiler.CompileAsync` both call
  `MonthlyRecurringRevenue.Calculate` — the live run's €746.56 appearing identically in both the
  dashboard and the freshly-compiled analytics row is that invariant holding under real data, not a
  coincidence.
- **`new_gyms` is an event count, not a population count — a real defect a review caught before it
  shipped**, and the design document itself was wrong until this task's predecessor corrected it (§3
  above; full account in the task-3 report).
- **`/dashboard/health` needed a second enforcement layer added after the fact.** The brief's own two
  sections disagreed (one named a `GetDashboardHealthQuery`, the other said to call
  `HealthCheckService` directly); the second was followed first, and review caught that it left the
  route with only one permission check where every other route has two. Worth remembering for any
  future endpoint that wraps a framework service directly: check whether the framework types are
  reachable from Application before assuming a direct call is the shorter path — here they were not
  (health-check packages resolve only from the ASP.NET shared framework), so the real fix needed a
  seam in Application plus a host-only adapter, not a query type calling the service straight through.
- **Live-run baselines are not always zero**, and a live run should say so rather than imply a clean
  slate. This run's dev database carried real state from earlier sessions; the split it demonstrates
  (dashboard moves, analytics does not) is exactly as convincing measured as a delta as it would have
  been from zero, and pretending otherwise would be the "smoother than it was" writeup this task was
  explicitly told not to produce.

## Administrator activation as built (ADR-53)

**Merged** (`c487012`), console half in GymOS-Platform (`d27e32a`). One migration:
`AddUserActivationTokens`. Design:
`docs/superpowers/specs/2026-08-12-admin-activation-email-design.md`.

1. **`IEmailSender`** in BuildingBlocks — a recipient, a subject, two rendered bodies, and nothing
   about identity, so `GymOS.Modules.Notifications` can absorb it as its Email channel sender rather
   than replace it. `SmtpEmailSender` (MailKit) when `Email:Enabled`, `LoggingEmailSender` otherwise —
   which is why the suite needs no SMTP server. **MailKit approved in `DEPENDENCY_POLICY.md` with
   `ForbiddenPackagesTests` in the same commit**, Infrastructure-only by R8.
2. **`UserActivationToken`** gets `RefreshToken`'s treatment exactly: 32 random bytes, SHA-256 at
   rest, single-use, time-boxed — 72h for an invitation, 1h for a reset. Issuing invalidates whatever
   the user held, so at most one link is live.
3. **Two orderings do the security work.** The token is spent *before* the password is policy-checked
   (otherwise a link stays replayable for its whole life to anyone willing to submit a rejected
   password — the attempt costs them nothing), and setting a password revokes every refresh token,
   because a reset is often prompted by the belief that somebody else is in.
4. **All four failure modes answer identically** — unknown, expired, already used, account gone or
   suspended: one `404`, one body. A distinct "expired" would confirm to an anonymous caller that the
   token had once been real. Asserted at the unit *and* HTTP layers, comparing everything but the
   per-request `traceId`.
5. **The send is after the commit and cannot undo the account work.** The outbox would be the right
   home and its processor does not exist; `resend-invitation` is the recovery path, which is why it is
   in scope. `RetentionJob` purges spent tokens.

### Decisions and traps found

- **`ActivationLinks` built its URL from an empty `ConsoleBaseUrl` when email was disabled**, turning
  every invitation into a 500. Caught by the integration suite (22 failures), not by review. There is
  now a development default, and the startup validation still demands an absolute URL when email is on.
- **Three convention guards fired and were answered rather than muted**: the concurrency-token rule
  (`UserActivationToken` exempt, with the reason — written once, consumed once, guarded by the
  aggregate), the permission-or-exemption rule, and the bearer-auth tripwire. All three now name the
  anonymous `set-password` operations explicitly.
- **`X-Invitation-Email: sent` also means "email is disabled"** — see "Known open items". Found while
  verifying the README against an instance that had email off.
- The raw token never reaches a log, a response body, or an audit row; the trail records
  `user.invitation_sent` / `user.password_reset_requested` / `user.password_set` instead.

## R14 as built

**Committed locally on `development`, not yet pushed — pushing is the maintainer's call.** Design
(approved 2026-08-14): `docs/superpowers/specs/2026-08-14-r14-tenant-isolation-design.md`. Plan:
`docs/superpowers/plans/2026-08-14-r14-tenant-isolation.md`. Business rules:
**`docs/BusinessRules/TenantIsolation.md`**. **R14 has a subject and a passing validation for the first
time.** No production table is tenant-scoped by this increment — the mechanism is proven against
`platform.tenant_probe`, a table that exists only for the tests. Members is next and will be the first
real subject.

1. **`ITenantContext` / `ITenantContextSetter` / `ITenantScoped`** (BuildingBlocks) — the acting tenant
   for a scope, read and write kept as separate interfaces so a handler cannot re-point its own scope
   even by accident. Deliberately not part of `ICurrentUser` (who the caller is and which tenant they
   act for are different facts). `TenantContext` is a one-shot setter: re-pointing to a different
   tenant throws, setting the same tenant twice is harmless.
2. **`TenantSessionInterceptor`** (Platform.Infrastructure) — writes `app.tenant_id` on **every**
   connection open, including an empty string for platform scope, because Npgsql pools physical
   connections and a "set it when there is a tenant" shape has an execution path where neither branch
   runs, leaking the previous tenant into the next borrower.
3. **`TenantIsolationValidator`** — the startup persistence validation R14 has always named and never
   had. Per `ITenantScoped` entity it asserts a `tenant_id` column, RLS enabled, RLS **forced**
   (`relforcerowsecurity`, not merely `relrowsecurity`), and at least one policy; it now also rejects a
   connected role reporting `rolsuper` or `rolbypassrls` — the guard the deployment's superuser
   connection needs (see "Known open items"). Fails the boot; does not swallow. Skips only while
   migrations are genuinely pending, the same first-boot guard `SuperAdminSeeder` already has.
4. **`TenantResolutionMiddleware`** (WebApi) — resolves `{gymId}` from the route after
   `UseAuthorization()`, checks the caller's grants (`UserRole.TenantId`: `null` = platform-wide, a
   value = that one gym), and sets the tenant. A caller with no grant for that gym, or a malformed
   `{gymId}`, gets **404, never 403** — the status code must not be an oracle for enumerating tenants.
5. **Tests** — a test-only `platform.tenant_probe` table and 12 integration tests (8 isolation + 4
   resolution). The owner-bypass test was proved by negative control: with
   `FORCE ROW LEVEL SECURITY` removed it reported `Assert.Single() Failure: The collection contained 2
   items`, then passed with the line restored — the exact silent failure the whole design exists to
   prevent, shown actually failing rather than assumed to.

**Gate numbers, re-measured at close-out, run individually to completion:** `dotnet build
GymOS.Backend.slnx` — **0 warnings, 0 errors**. `dotnet format --verify-no-changes
GymOS.Backend.slnx` — **exit 0**. `dotnet test tests/GymOS.IntegrationTests --no-build` — **301
passed**. Whole solution (`SharedKernel` 131 · `Platform.Domain.UnitTests` 434 ·
`Platform.Application.UnitTests` 247 · `BuildingBlocks.UnitTests` 66 · `ArchitectureTests` 153 ·
`Platform.Infrastructure.UnitTests` 67 · `IntegrationTests` 303) — **1401 passed**.

### Three open items this increment produced rather than closed

All three were in "Known open items" below and in `docs/BusinessRules/TenantIsolation.md`; none was
fixed by R14's own mechanism increment. (The third was added by the increment's final review on
2026-08-15, which found it undocumented rather than newly created.) **The first of the three is now
closed — see "Database roles (R14 close-out) as built", directly below.**

- ~~**The deployment connects to PostgreSQL as a superuser**, which voids `FORCE ROW LEVEL SECURITY`
  entirely — a superuser bypasses row-level security unconditionally, `FORCE` or not. The validator's
  new role check is the guard, and it has never fired because no entity implements `ITenantScoped` yet;
  it will fire, loudly, the day `members` does. Changing which role the API connects as is an open
  decision for the maintainer.~~ **Closed.** The API now connects as `gymos_app`
  (`NOSUPERUSER NOBYPASSRLS`, owns nothing); the validator's role check is unconditional and runs on
  every boot rather than waiting on `ITenantScoped`.
- **A gym-scoped grant cannot currently be assigned through the API at all.**
  `SetUserRolesCommandHandler` rejects any non-platform-scoped role and only ever writes
  `UserRole.TenantId = null`. Every grant any role holds today is platform-wide, which is the real
  reason `TenantResolutionMiddleware` is inert against the existing suite. The Members increment needs
  a way to create a gym-scoped grant; it does not exist yet.
- **`TenantScope` was specified and not built.** The design's §4 lists it as a component and the
  business-rules doc named it as an existing seam; nothing defines it. Deliberate — no consumer yet —
  but unrecorded until the final review. Jobs take a fresh DI scope per gym by hand until it lands.

### `ARCHITECTURE-ADDENDUM.md`'s R14 row

Checked, not changed. `GymOs-Specification/ARCHITECTURE-ADDENDUM.md` §8.1's engineering-rules table
(R1–R17) uniformly pairs each rule with the mechanism that is *meant* to enforce it — "R14 | Every
tenant-scoped table: `tenant_id` + RLS policy | startup persistence validation" — and that row already
names the mechanism this increment built. The table carries no column, marker, or convention anywhere
in it distinguishing a rule whose enforcer is built from one whose enforcer is only specified — R1
through R17 are all worded identically regardless of build status, because the document is a frozen
architecture specification, not a build-status tracker. Marking only R14 "enforced" would invent a
field the other sixteen rows do not have. Left as written; the fact that the mechanism now exists lives
in this file and in `docs/BusinessRules/TenantIsolation.md` instead.

## Database roles (R14 close-out) as built

**Committed locally on `development`, not yet pushed — pushing is the maintainer's call.** Design:
`docs/superpowers/specs/2026-08-15-database-roles-design.md`. This is the deployment half of R14: the
mechanism above was proven correct and then shipped against a connection that made it irrelevant — the
application connected as `gymos`, the bootstrap superuser the `postgres` image creates, and PostgreSQL
exempts a superuser from row-level security entirely, `FORCE` included. This increment is what makes
R14 actually filter a row in the Docker deployment.

1. **Three roles**, replacing the single superuser. `gymos_admin` — superuser, now `POSTGRES_USER`, for
   humans and maintenance only. `gymos_migrator` — `NOSUPERUSER NOBYPASSRLS`, owns the database, its
   schemas, and every table; runs DDL; used only by the `migrator` service below. `gymos_app` —
   `NOSUPERUSER NOBYPASSRLS`, owns nothing; used by the API at runtime. Created by
   `docker/postgres/initdb/10-roles.sql`, which reads both passwords through psql's `\getenv` rather
   than shell-interpolating them into SQL — verified directly against `postgres:17-alpine` that a
   password containing a single quote cannot break the statement or inject anything.
2. **D1 — the migrator owns; the app owns nothing.** An owner can execute
   `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` and switch its own isolation off in one statement — well
   within reach of the SQL injection row-level security mostly exists to contain. `gymos_app` owns
   nothing and holds no DDL rights, so the identical statement is refused outright. Proved directly,
   not merely asserted: the statement is refused (SQLSTATE 42501) on the `gymos_app` connection and
   succeeds on the `gymos_migrator` connection running the same statement as the control.
3. **D2 — default privileges, not per-table grants**, declared with no `IN SCHEMA` clause so every
   future schema and table is covered automatically, since `platform` itself is created by a migration
   and an `IN SCHEMA platform` form could only run after the fact. **The constraint this creates:**
   default privileges attach only to objects created *by* `gymos_migrator` — anything that ever creates
   a table as another role produces a table `gymos_app` cannot read. Holds by construction today,
   because only the migrator holds DDL rights; recorded in `docs/BusinessRules/TenantIsolation.md` so it
   does not stop holding quietly.
4. **Migrations moved out of the API into a one-shot `migrator` compose service**, running the same
   image with a new `Database:MigrateOnly` host mode: apply migrations, exit, never start the web
   server. `Database:MigrateOnStartup` is now `false` everywhere, development included, and `api`
   declares `depends_on: { migrator: { condition: service_completed_successfully } }`. Seeders and
   `TenantIsolationValidator` stay in the API's own startup path as `gymos_app` — the role whose
   isolation is worth validating, and seeding is DML, not DDL.
5. **`TenantIsolationValidator`'s connected-role check is now unconditional.** It used to run only once
   some entity implemented `ITenantScoped`, which was also the hole through which an entity that forgot
   the marker switched the whole control off; it now runs, and can refuse the boot, on every start. It
   also asserts the connected role does not *own* the tenant-scoped tables it checks, via
   `pg_has_role(current_user, c.relowner, 'USAGE')` rather than a name comparison — deliberately, so a
   future rename of either role does not silently disable the check.
6. **The integration harness moved off the superuser.** Making the role check unconditional meant it
   now runs in every integration test host, and Testcontainers' bootstrap role is a superuser — left
   alone, the whole suite would fail to boot. `PostgresFixture` now provisions a non-superuser role per
   test database; most hosts get a single role that owns its own database (cheap, and enough to satisfy
   the check), and the tenancy suite specifically gets the full two-role shape, because it is the one
   asserting the ownership separation the rest of the suite does not touch.

**Gate numbers, re-measured at this close-out, run individually to completion:** `dotnet build
GymOS.Backend.slnx` — **0 warnings, 0 errors**. `dotnet format --verify-no-changes GymOS.Backend.slnx`
— **exit 0**. `dotnet test tests/GymOS.IntegrationTests` — **311 passed**. Whole solution
(`SharedKernel` 131 · `Platform.Domain.UnitTests` 434 · `Platform.Application.UnitTests` 247 ·
`BuildingBlocks.UnitTests` 66 · `Platform.Infrastructure.UnitTests` 67 · `ArchitectureTests` 153 ·
`IntegrationTests` 311) — **1409 passed, 0 failed**. `docker compose down -v` (destroying the existing
volume) followed by `docker compose up -d` brought up postgres, mailpit, and api healthy and migrator
exited `0` on the fresh volume; `docker compose ps -a` showed all four as expected, and
`SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname LIKE 'gymos%'` against the freshly
initialised database returned exactly `gymos_admin` (`t`/`t`), `gymos_migrator` (`f`/`f`), and
`gymos_app` (`f`/`f`) — the three roles with the attributes the design calls for.

**Documentation:** `docs/BusinessRules/TenantIsolation.md` now carries the roles table, D1's and D2's
reasoning, and closes the superuser open item (the gym-scoped-grant and `TenantScope` items are
untouched by this increment). `docs/runbooks/managed-postgres-roles.md` is new — the same three roles
on a managed instance with no `/docker-entrypoint-initdb.d`, opening with the operator checking their
own `rolsuper`/`rolbypassrls` rather than assuming Azure's administrator is safe by default, and
including the ownership-transfer procedure (`REASSIGN OWNED`, database/schema ownership, default
privileges, and the explicit `GRANT … ON ALL TABLES IN SCHEMA platform` that default privileges cannot
reach for objects that already exist). Not provisioned against anything — there is no Azure deployment
yet. `docs/Infrastructure/DOCKER.md` and `.env.example` are brought back in line with the new roles,
having been deliberately left alone by the R14 mechanism increment as out of its scope.

**What this increment did not touch, on purpose:** no RLS policy changed — only *who connects* changed;
`SetUserRolesCommandHandler` still cannot issue a gym-scoped grant; and `TenantScope` is still owed, not
built. All recorded, unchanged, under "Known open items" below.

## Database roles — final-review fixes as built

**Committed locally on `development`, not yet pushed.** The whole-branch review found what the
per-task reviews structurally could not: **the increment took DDL rights away from the API process
without auditing what runtime DDL that process still performs.** Two plpgsql functions installed by
earlier migrations issue DDL as the *calling* role, and both broke the day the API stopped being a
superuser — invisibly, because the integration harness runs a single role that owns everything.

1. **`platform.next_invoice_number` could not issue the first invoice of any year.** It creates
   `platform.invoice_number_seq_<year>` on first use; `gymos_app` has `USAGE` but no `CREATE` on
   `platform`, and the function's own handler catches only `duplicate_object`/`unique_violation`, so
   `42501` propagated out of `InvoiceNumberGenerator` as a **500**. Proved both ways against a
   freshly wiped compose stack, through the API rather than by reasoning: assigning a subscription
   returned **HTTP 500** with `Npgsql.PostgresException 42501: permission denied for schema platform`
   in the log, and after the fix the same call returned **HTTP 200** and the invoice
   `2026-000001`.
2. **`platform.ensure_audit_log_partitions` failed silently and would have done so forever.** Same
   missing `CREATE`, but its `EXCEPTION WHEN others` swallowed the privilege error and raised a
   warning, so the function returned 0 and `AuditLogPartitionMaintainer` logged **Information**
   "0 created" — its Error path could never fire. Every audit row would have landed in
   `audit_logs_default` permanently once the `PartitionAuditLogs` migration's built-in three-month
   lookahead elapsed.
3. **Both fixed by a new migration, `SecureRuntimeDdlFunctions`**, using `CREATE OR REPLACE FUNCTION`
   so databases that already ran the originals are fixed too. Both become `SECURITY DEFINER` — they
   execute as their owner, `gymos_migrator`, which holds the `CREATE` they need — with the two
   things that makes mandatory: a **pinned search path** (`SET search_path = pg_catalog, pg_temp`;
   without it a caller can point an elevated function at a schema it writes and have its own code run
   as the owner) and **`REVOKE ALL … FROM PUBLIC` plus an explicit `GRANT … TO gymos_app`**, because
   `CREATE FUNCTION` grants `EXECUTE` to `PUBLIC` and on a `SECURITY DEFINER` function that lends the
   owner's DDL rights to every role in the database. The grant is guarded on the role existing, so it
   is inert in harnesses and managed instances that name their serving role differently; the runbook
   carries the statement for those.
4. **The `WHEN others` swallow is narrowed** to `check_violation OR duplicate_table` — rows already in
   the DEFAULT partition for the range (the cause the original comment describes) and the creation
   race the catalog check above it does not close. `insufficient_privilege` and everything else now
   propagate to the maintainer's Error path.
5. **Readiness now reports unhealthy while migrations are pending.** `/health/ready` used
   `AddDbContextCheck` → `CanConnectAsync`, which is true of an empty database — so a bare
   `dotnet run` (development sets `MigrateOnStartup: false` and nothing sets `MigrateOnly`) booted
   **green** with R14 off, all three seeders skipped and both sweeps skipped. Worse than that was
   measured: Npgsql treats `3D000` as "the database does not exist" rather than an error, so a host
   pointed at a **nonexistent** database also boots green. One check named `database`
   (`DatabaseReadinessHealthCheck`) replaces `AddDbContextCheck` and asks both questions; registering
   a *second* check alongside it is what does not work here — the health service runs registrations
   concurrently in separate scopes, so every host opens a second physical connection and the suite
   took the container past `max_connections=300` (122 failures with `53300`).
6. **Corrections to reasoning that was wrong rather than merely incomplete.** `10-roles.sql`, the
   design's D2 and the runbook all claimed no sequence grants were needed because
   `ApplyClientAssignedKeys` makes every key client-assigned — true of EF-generated keys, false of
   the database, since hand-written migration SQL creates sequences. All three corrected and
   `ALTER DEFAULT PRIVILEGES … GRANT USAGE, SELECT ON SEQUENCES` added (hygiene, not the fix — the
   `SECURITY DEFINER` function needs no caller privilege on its sequence). The runbook's claim that
   `REASSIGN OWNED` does not cover schemas is corrected: it does; databases and tablespaces are the
   exceptions. `GYMOS_MIGRATE_ON_STARTUP` is gone from `docker-compose.yml`, `.env.example` and
   `DOCKER.md` — an older `.env.example` shipped it as `true`, and a stale `.env` would have had the
   API attempt DDL as `gymos_app` on every boot.
7. **Two test changes worth naming.** `MigrateOnlyTests` now asserts SQLSTATE `42501` rather than
   "something threw" — and the removal experiment the review asked for produced a **different**
   answer than the review predicted: with the `MigrateOnly` branch deleted the host throws *nothing
   at all* (six skip warnings and a green boot), so the old `ThrowsAnyAsync<Exception>` did fail. It
   was a weak guard, not a vacuous one. `ApiEndpointTests` now migrates its host: it had been
   asserting `/health/ready` = 200 against whatever an earlier suite happened to leave in the shared
   database, which the old connectivity-only check could not tell apart from a correct one.

**Gate numbers:** `dotnet build GymOS.Backend.slnx` — **0 warnings, 0 errors**. `dotnet format
--verify-no-changes GymOS.Backend.slnx` — **exit 0**. `dotnet test tests/GymOS.IntegrationTests` —
**312 passed** (311 before; the tenancy suite gains one test that watches both functions work as the
app role, deliberately one method rather than four — this suite's connection budget is that tight).
Whole solution — `SharedKernel` 131 · `Platform.Domain.UnitTests` 434 ·
`Platform.Application.UnitTests` 247 · `BuildingBlocks.UnitTests` 66 ·
`Platform.Infrastructure.UnitTests` 67 · `ArchitectureTests` 153 · `IntegrationTests` 312 —
**1410 passed, 0 failed**.

## SQL Server migration as built (ADR-54, `feature/sqlserver-migration`, 2026-08-24)

**Not merged, not reviewed, and authorised by an ADR that is still `Proposed`.** Two commits:
`4b29293` is an explicit non-building checkpoint (four test files still named
`PostgresException`/`PostgresErrorCodes`), `002740f` completes it. Read both commit messages before the
code — they are unusually good, and each of the engine differences below is written up where it bites
rather than in a summary.

**Provider and packages.** Npgsql, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.OpenTelemetry` and
`Testcontainers.PostgreSql` are gone; `Microsoft.EntityFrameworkCore.SqlServer`,
`Microsoft.Data.SqlClient`, `OpenTelemetry.Instrumentation.SqlClient` and `Testcontainers.MsSql` replace
them. `docs/Engineering/DEPENDENCY_POLICY.md` and `ForbiddenPackagesTests` were updated **in the same
commits**, per the standing rule — Npgsql is out of the approved list and out of `ProviderSdkPrefixes`.
A licensing improvement in passing: the Npgsql family shipped under the PostgreSQL licence, the
replacements under MIT. The migrations history table moves to the `platform` schema so the migrator
principal can write it. **`EnableRetryOnFailure` is deliberately off** — see "Known open items".

**Runtime adapters.** Advisory and job locks → `sp_getapplock` (Transaction and Session owners);
database clock → `SYSUTCDATETIME()`; tenant session → `sp_set_session_context`;
`next_invoice_number` and `ensure_audit_log_partitions` → stored procedures `WITH EXECUTE AS OWNER`,
because SQL Server functions cannot issue DDL and the app principal holds none; `FOR UPDATE`/`FOR SHARE`
→ `UPDLOCK`/`REPEATABLEREAD` hints; `ON CONFLICT DO NOTHING` → `INSERT … WHERE NOT EXISTS` under
`UPDLOCK, HOLDLOCK`; `ILIKE` → `LIKE` against a `Latin1_General_100_CI_AS` database.

**Types.** `jsonb` → `nvarchar(max)`; `numeric` → `decimal`; `xmin` → a real `rowversion` column
surfaced as `ulong` through `NumberToBytesConverter`, declared once in
`ConcurrencyConventions.HasGymOSRowVersion()` and asserted for every mutable entity by
`ConcurrencyModelTests` — under PostgreSQL the declaration was free, so getting it wrong now means a
table with no concurrency control at all. The API's version token stays an opaque decimal string, which
is why `contracts/openapi-v1.json` has no diff. `AreNullsDistinct(false)` is dropped because SQL Server
already treats NULLs as equal in a unique index.

**Migrations.** The seventeen Npgsql migrations are **deleted rather than converted**, replaced by a
scaffolded `InitialSqlServer` and a hand-written `RuntimeDatabaseProgrammability` carrying the RLS
predicate, invoice numbering, and the audit-log partition function, scheme and maintenance procedure.
`002740f` restored four pieces of hand-written DDL that deleting the old migrations had silently
dropped: the plan-price, MRR-currency and idempotency-claim unique indexes, and the single-currency
CHECK on invoices.

**Docker.** SQL Server 2022 Developer replaces PostgreSQL 17. The image has no
`docker-entrypoint-initdb.d`, so initialisation is an explicit idempotent **`db-init`** service that the
migrator gates on — and unlike the PostgreSQL scripts it replaces, it runs on every `up` rather than
only against an empty data directory, so **changing a password in `.env` does not rotate it**. pgAdmin
is removed rather than swapped (it cannot speak to SQL Server), leaving the `tools` profile empty;
Mailpit is unaffected, having been profile-less since ADR-53.

**Roles.** The three-principal split survives intact: `sa` for humans only, `gymos_migrator`
(`db_ddladmin` + reader/writer + `ALTER ANY SECURITY POLICY`, one-shot `migrator` service),
`gymos_app` (reader/writer and nothing else). `TenantIsolationValidator` refuses to boot on a principal
holding `sysadmin`, `db_owner`, `db_securityadmin`, `ALTER ANY SECURITY POLICY` or `CONTROL`, which is
what makes the split load-bearing rather than hygienic. **`FORCE ROW LEVEL SECURITY` has no counterpart
and needs none:** a SQL Server security policy filters the table owner and `sa` too, where a PostgreSQL
owner bypassed RLS unless forced. This is strictly stronger than what it replaces.

**Five engine differences, four of them found by running the stack.** `READ_COMMITTED_SNAPSHOT` is on,
because SQL Server's READ COMMITTED takes shared locks and `SetGymFeaturesCommand`/`SetPlanFeatureCommand`
deadlocked through `plan_features` — a table neither locks on purpose — returning a 500 from the
concurrency test; RCSI is also the Azure SQL default, so development and production now agree, and the
explicit hints still block exactly where they ask to. `gymos_app` needs `VIEW DEFINITION ON
SCHEMA::platform`, because catalog views are permission-filtered and `sys.security_predicates` otherwise
reads as empty on the app connection, failing the isolation check closed for a reason that was not true.
The container needs `CAP_NET_BIND_SERVICE` — not to bind a port, but because the kernel refuses to exec
a binary whose file capabilities fall outside the bounding set. `CREATE LOGIN` will not take a
parameterised password, so it is built with `QUOTENAME(…, quote)` and **a bootstrap password may not
contain a single quote**. The fifth is **not written up anywhere and should be**: the database-wide CI
collation makes the client-supplied `idempotency_key` match case-insensitively, where `lc-collate=C`
compared it byte-for-byte. Negligible for random keys; it is still a behaviour change.

**Two traps recorded in the commits, both worth carrying forward.** EF Core silently adds
`WHERE [tenant_id] IS NOT NULL` to a unique index over a nullable column, which would have exempted
every platform-scope role from the constraint meant to catch duplicates — `HasFilter(null)` suppresses
it, and **any future such index needs the same**. And `sys.database_principals` is permission-filtered,
so a migration guarded on seeing `gymos_app` skips its GRANT without error; grants therefore live in
bootstrap as `GRANT EXECUTE ON SCHEMA::platform`, which is also the `ALTER DEFAULT PRIVILEGES` analogue
for future modules.

**Test infrastructure.** `PostgresFixture` → `SqlServerFixture` (+ `TestDatabase`, `MigrateOnlyTests`).
Databases are created with small explicit files under SIMPLE recovery and the buffer pool is bounded,
because a hundred default-sized databases in one instance was enough to make the engine stop answering
mid-query and fail thirty tests in suites that had nothing to do with it. **No test was skipped or
deleted to make the suite pass** — checked: the diff adds no `Skip =` and no `TODO`/`FIXME`.

**Console side.** `GymOS-Platform` `2c45888`, confined to the E2E suite: `seed-analytics.sql` rewritten
(`generate_series` → recursive CTE, `gen_random_uuid` → `NEWID`, dates off `SYSUTCDATETIME` so a
non-UTC container cannot drift a day), `global-setup.ts` shells `sqlcmd -b` instead of `psql`, and CI
gained a step to apply the backend's own initdb scripts because the mssql image builds neither database
nor principals from environment variables. The app itself is untouched.

**Gate numbers — the author's, from `002740f`, not re-measured here:** restore, build,
`dotnet format --verify-no-changes` and test green; **1,420 passed, 0 failed**, of which **314
integration** against SQL Server 2022 via Testcontainers; the composed stack boots from a clean checkout
and the seeded administrator signs in. Not reproducible with a local SDK: `global.json` pins
**10.0.302** and only **10.0.301** is installed. Reproduced instead in the
`mcr.microsoft.com/dotnet/sdk:10.0.302` container — the pinned SDK exactly: **build 0/0, `dotnet
format` exit 0, 1,137 unit tests green**. **The 314 integration tests remain unrun — run them before
promoting.**

## Environment model as built (2026-08-24, same branch)

**Three environments, three databases, and four independent things enforcing the separation.**
Documented in full in **`docs/Infrastructure/ENVIRONMENTS.md`** — read that, not this summary.
`Development`→`GymOS_Local`, `Staging`→`GymOS_UAT`, `Production`→`GymOS_Production`; the name is what
makes the pairing checkable, which is the whole reason they are not one name on three servers.

**`DatabaseEnvironmentGuard`** (`Platform.Infrastructure/Persistence`) runs in `Program.cs` above the
`Database:MigrateOnly` branch, so it covers the migrator too — the process that applies DDL is the one
that must least be allowed to do it to the wrong environment. It **denies cross-environment pairings**
rather than prescribing a database per environment, and that is not laziness: the integration suite
boots the real host as `Development`, `Staging` **and** `Production` against generated Testcontainers
databases (`OpenApiExposureTests` pins what an operator gets by doing nothing), so a prescriptive rule
would have needed a bypass switch that production could also set. Messages name the environment, the
database found and the database expected, and **never the connection string** — it carries a password.
Verified live in both directions: a `Production` host and a `Staging` host each refused `GymOS_Local`
and died before migrating, and `Development`+`GymOS_Local` booted to `/health/ready` 200.

The other three layers: **`SqlServerFixture`** refuses to create or open any managed name, so the suite
cannot be the thing that names one; **`Assert-LocalDatabaseTarget`** (`scripts/GymOS.LocalDatabase.psm1`)
is stricter still, requiring `GymOS_Local` on a *local server* — which is what catches a database called
`GymOS_Local` sitting on the UAT server, where the name alone would pass; and the **deploy workflows**
grep the migrator connection string for the expected database before connecting.

**`GYMOS_DB` is now required in `docker-compose.prod.yml`** (`${GYMOS_DB:?…}`), so production cannot
inherit a default. Proven both ways: prod interpolates with it, and refuses without it.

**Scripts** (`scripts/`, all three ASCII-only — PowerShell 5.1 reads `.psm1` as ANSI and mangled
em-dashes broke the parse first time): `populate-all.ps1` and `reset-local-db.ps1`, sharing one guard.
Verified end to end against a live stack: reset drops, `db-init` rebuilds, the migrator applies, and
populate seeds — `1` user, `5` roles, `16` permissions, `12` features, and the seeded administrator
signs in (HTTP 200). Idempotent: a second `populate-all` left all four counts identical.

**Four defects found by running these rather than by reading them**, each fixed and re-verified:
`SET SINGLE_USER` raced the API's reconnect, so the drop failed *and left the database stuck in
SINGLE_USER* (now stops the API first, with a T-SQL `TRY/CATCH` that restores `MULTI_USER`); `THROW`
needs a preceding `;`; `docker compose` writes progress to **stderr**, which Windows PowerShell turns
into terminating errors under `$ErrorActionPreference='Stop'` whenever a caller merges streams (hence
`Invoke-Native`, which fails on exit code instead); and reset passed `-SkipStack` to populate after
stopping the API, so it reported "freshly built and populated" against a database with **zero** users,
roles and features. That last one is the one worth remembering: nothing failed, it simply was not true.

**Branches: `staging` is retired.** CI now builds `[main, development]`. It sat between them as a third
promotion step nothing was ever promoted through — it last moved on 13 August. The ref still exists.
**Only this repository was changed**; the other six still trigger on `staging`.

**Deployment is prepared and UNVERIFIED.** `deploy-uat.yml` (push to `development`) and
`deploy-production.yml` (push to `main`) are committed, valid YAML, and **gated off** behind the
repository variables `UAT_DEPLOY_ENABLED` / `PRODUCTION_DEPLOY_ENABLED`, so merging changes nothing
while no infrastructure exists. Production deploys the SHA-tagged image UAT already tested rather than
rebuilding, and its migrate job sits behind the `Production` GitHub Environment's required reviewers.
**The `deploy` step in both is a deliberately failing placeholder** — no Azure resource exists, and an
`azure/webapps-deploy` block against invented resource names would look finished and fail later.

## Standing rules (non-negotiable, enforced)

- **Architecture is frozen.** Deviations need a superseding ADR in
  `GymOS-Specification/ADR-CHANGELOG.md` *before* implementation (ADR-50/52 are the
  templates). Do not silently modify decisions.
- **Increment protocol:** explain design → implement → verify (build, tests, live run)
  → summarize → **stop for review**. Never jump ahead.
- **Dependency policy:** `docs/Engineering/DEPENDENCY_POLICY.md` and
  `ForbiddenPackagesTests` change **in the same commit**. Allowlist, not blocklist.
- **Golden Rule** (`GymOS-Specification/PRODUCT-PRINCIPLES.md`): every feature must
  reduce work for the gym owner; trim scope per module before implementing.
- House patterns already established: `IClock` only (BCL clocks are analyzer-banned);
  time flows inward as parameters; business failures are `Result`, invariant breaches
  are `DomainException`; events raised in aggregates only; source-generated
  `LoggerMessage` for logging; migrations never hand-edited (generated-code editorconfig).

## Repo map (ADR-52) — org: github.com/GymOS-Management

GymOS-Backend (this repo — THE .NET modular monolith, active) · GymOS-Specification
(frozen docs) · GymOS-Platform (Super Admin Next.js app, active, `v0.1.0`) · GymOS-Admin
(gym staff app) · GymOS-Mobile · GymOS-Gateway · GymOS-Website.
Note the trap: `GymOS.Platform.*` *projects in this repo* = platform-scope modules,
unrelated to the GymOS-Platform frontend repo.

## Fresh-machine setup

Just to run it — nothing but Docker required:

```
git clone https://github.com/GymOS-Management/GymOS-Backend
cd GymOS-Backend
docker compose up -d               # SQL Server 2022 + db-init + migrator + API + mailpit, :5080
                                   # db-init creates the database, both principals and the two schemas
                                   # and exits 0; migrator then applies migrations and exits 0; then api
                                   # sign in: admin@gymos.local / gymos-dev-admin-password
```

**On a clone from before 2026-08-24 (the SQL Server migration), `docker compose down -v` first.** Not
for the reason the PostgreSQL version needed it: the volume is a *different* volume
(`gymos-postgres-data` → `gymos-mssql-data`), so the old data is simply no longer mounted rather than
in the way. What does need clearing is any still-running `gymos-postgres` container and, more
importantly, **your `.env`**.

**A stale `.env` is the likeliest first-boot failure, and there are two distinct ways it bites.** The
`POSTGRES_*` variables are now unread, so an old `.env` full of them is inert and misleading rather than
harmful. The two that do break things: **`GYMOS_DB_CONNECTION` pointing at the team's shared Neon
PostgreSQL instance** is now a connection string in the wrong dialect — `Microsoft.Data.SqlClient`
rejects `Host=`/`Username=` outright — so clear it or replace it with a `Server=…,1433;…` string; and
any password you set yourself must satisfy **SQL Server's complexity policy** (8+ characters, three of
upper/lower/digit/symbol), because the engine refuses to start at all if the `sa` password fails it and
`CREATE LOGIN` refuses the others. **A bootstrap password may also not contain a single quote** —
`sqlcmd -v` substitution is textual, so the quote breaks the statement before the server can escape it.
It fails loudly at first boot (`db-init` runs with `-b` and exits non-zero, and compose then refuses to
start the migrator or the API behind it).

**`db-init` is idempotent and runs on every `up`, unlike the initdb scripts it replaces** — which also
means **changing a password in `.env` does not rotate it**. Rotate with `ALTER LOGIN`, or
`docker compose down -v` for a throwaway environment.

To work on it — API on the host, database in a container:

```
dotnet tool restore                # pinned dotnet-ef
docker compose up -d db-init       # brings mssql up with it, then creates the database + principals
                                   # `up -d mssql` alone is NOT enough: no database, no logins
dotnet test GymOS.Backend.slnx     # expect 1420 green (needs Docker) — see the test-host warning below

# Migrations no longer apply on API start-up in any environment (Database:MigrateOnStartup is false
# everywhere) — run them once by hand, as the migrator principal, before the first `dotnet run`:
dotnet ef database update --connection "Server=localhost,1433;Database=gymos;User Id=gymos_migrator;Password=Gymos_dev_migrator_password1;Encrypt=True;TrustServerCertificate=True" \
    --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi

# Optional: an account to sign in with. Without it the seeder warns and creates nothing.
dotnet user-secrets set "Seed:SuperAdmin:Email" "you@example.com"    --project src/Host/GymOS.WebApi
dotnet user-secrets set "Seed:SuperAdmin:Password" "<12+ chars>"     --project src/Host/GymOS.WebApi

dotnet run --project src/Host/GymOS.WebApi   # seeds only now; migrations already applied above; :5080/health/ready
```

`appsettings.Development.json` connects the host-run API as `gymos_app`, matching the container's own
connection string — so a bare `dotnet run` against the local sidecar exercises the same non-owner
principal the Docker deployment does, never `sa`. It differs from the container's string in one place
only: `Server=localhost,1433` rather than `Server=mssql,1433`, because the host reaches the sidecar over
the published port instead of the compose network. **`db-init` must have run at least once** or neither
the database nor the login exists.

⚠️ The migration shipped without updating this file — it still held the Npgsql-syntax
`Host=localhost;Port=5432;…` string, which `Microsoft.Data.SqlClient` rejects outright, so `dotnet run`
and Visual Studio F5 both failed at startup. **Fixed 2026-08-24**, and the fix is not merged upstream
yet.

The JWT signing key has a **development-only** value in `appsettings.Development.json`
(public, in version control, loaded only when `ASPNETCORE_ENVIRONMENT=Development`) —
the same treatment the dev database password already gets. Every other environment must
supply `Security:Jwt:SigningKey`; startup fails fast if it is missing or under 32 bytes.

**Port 1433 collision:** if a native SQL Server or LocalDB is installed locally it competes with the
container for 1433 and wins, producing a login failure rather than a port conflict for whichever
principal tried to connect. Set `MSSQL_PORT=1434` in `.env` (the containerised API is unaffected — it
reaches the database over the compose network), or stop the local service.

Environment facts: .NET 10 SDK pinned via `global.json` (ADR-50); solution is
`GymOS.Backend.slnx`; local dev DB credentials live in `appsettings.Development.json`
matching `docker-compose.override.yml` (dev-only, deliberately public — but see the warning above:
the file's connection string was not migrated). **The SDK pin is `10.0.302` with
`rollForward: latestFeature`, so a machine carrying only `10.0.301` cannot build the solution at all** —
`dotnet build` exits with "A compatible .NET SDK was not found" before compiling anything. Install
10.0.302 or later in the same feature band. The data volume is now `gymos-mssql-data`; the PostgreSQL-era
`gymos-postgres-data` (itself renamed from `gymos-pgdata` in M0.X) is still on disk but no longer
mounted by anything — see DOCKER.md troubleshooting. There is **no database console in the stack**:
pgAdmin is gone and the `tools` profile is empty, so use an external client against
`localhost,1433` — `sa` / `Gymos_dev_sa_password1`, trust the server certificate.

## Next step (resume here)

### As of 2026-08-24 — read this first

`feature/sqlserver-migration` exists in **both** `GymOS-Backend` (`002740f`, 62 commits ahead of `main`)
and `GymOS-Platform` (`2c45888`). Neither is merged. Nothing has been reviewed. **Do these in order —
the first two are cheap and the third is what everything else waits on.**

1. **Get ADR-54's rationale from Konstantinos and then approve or reject it.** The ADR is drafted in
   `GymOS-Specification/ADR-CHANGELOG.md` as **`Proposed`**, with everything it supersedes and everything
   it leaves owed enumerated — but its "why" is a marked placeholder, because no commit, doc or spec in
   the branch states one. **Do not approve it by filling that in on his behalf.** If the reason turns out
   to be weaker than "stay on PostgreSQL", this is the cheapest moment in the project's life to find
   that out: nothing is merged.
2. ~~**Fix `src/Host/GymOS.WebApi/appsettings.Development.json`.**~~ **Done 2026-08-24** — it held a
   PostgreSQL connection string, so `dotnet run` and Visual Studio F5 failed at startup. Verify it with a
   host run once the SDK is installed; the string itself was proved to parse, but the app was never
   started with it.
3. **Run the 314 integration tests.** Everything else is now measured: build 0/0 and 1,137 unit tests
   green, via the SDK container (see "Known open items" for the command — no local SDK needed, which
   matters if you are on a machine without admin rights). The integration suite is the one gate still
   outstanding, it needs a Docker socket inside that container, and it is the suite that would actually
   exercise the persistence layer the migration replaced. **A local SDK 10.0.302+ is still worth
   installing** — `dotnet run`, F5 and `dotnet ef` all need one.
4. **Then review the branch, and review it as a security change.** It moves tenant isolation to a
   different enforcement primitive, re-derives the three-principal split, adds a metadata-visibility
   grant to the application principal, and changes string equality database-wide. Those four are exactly
   where a subtle regression would hide, and R14's whole point is that isolation is provable.
5. **Merge to `development`, not `main`.** 60 of the 62 commits are the R14/tenancy work from
   2026-08-15; `main` has never seen them.
6. **Two small things to fold into the review fixes:** write up the fifth engine difference (the CI
   collation makes `idempotency_key` match case-insensitively — nothing documents it), and correct
   `IdempotencyRecordConfiguration`'s XML doc, which still claims the claim index needs a `COALESCE`
   sentinel and lives in the deleted `AddIdempotencyRecords` migration. Neither is behavioural; both are
   the kind of comment this repo does not leave wrong.

**Everything owed before 2026-08-24 is still owed**, and none of it is affected by the migration except
in needing to be re-run against SQL Server: M4.1's multi-agent review gate, M5's whole-branch review,
F3's review and its unwritten `F3-AS-BUILT.md`, Konstantinos's API-reference increment, audit finding 7
(concurrent failed logins collide on the user row's version → 409 on `/auth/login`, under-counted
lockout), then **M6** (monitoring, settings). R14's two open items — no gym-scoped grant can be issued
through the API, and the gym-scoped-grants design has never been re-read — are unchanged, and R14 still
has no production tenant-scoped table; Members is next and will be the first.

### Previously, as of 2026-08-15, end of session

Three commits landed today and are **pushed** to `origin/development`: `c86a02c` (two parked
documentation fixes), `25136f3` (`TenantScope`), `59c9e38` (the gym-scoped-grants design spec).
Working tree clean, `development` in sync with `origin`.

**Do this first when you come back:**

1. **Review `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md`.** It is an approved design
   that nobody has re-read with fresh eyes. The two decisions most likely to be wrong are **D7** (per-gym
   roles provisioned with the gym, seeded from a code-canonical list) and **D9** (the endpoint shape,
   `PUT /gyms/{gymId}/users/{userId}/roles`). Until that review happens the spec is one person's opinion.
2. **Confirm the one unverified claim in it** (§6): that `WebApplication` inserts `UseRouting` at the
   head of the pipeline, so route values are populated at `UseAuthentication()` time. Taken from
   framework documentation, **not observed**. The whole of D1 rests on it, and D1 is the spine of the
   design. Cheapest check: log `HttpContext.Request.RouteValues` from a middleware placed there.
3. **Then either** start Members (which carries the grant work as steps 1–5 of that spec) **or** land
   spec steps 1–2 alone — the pipeline reorder and tenant-aware evaluation. Those two close the
   privilege-escalation risk and leave the platform exactly as capable as it is today, so they are safe
   to ship without the rest.

**Not verified today, and worth knowing before trusting anything above:** Docker Desktop was not running
on this machine, so **the 312 integration tests were not run all session**. Everything reported is the
1104 non-Docker tests (SharedKernel 131 · BuildingBlocks 72 · Domain 434 · Application 247 ·
Infrastructure 67 · Architecture 153), each project run individually to completion with no `Aborted`,
plus build 0 warnings and `dotnet format` exit 0. **Run the integration suite before the next merge to
`staging`.**

**Also still true:** `development` is **59 commits ahead of `staging`** in this repo and Platform is 26
ahead. Nothing has been promoted since 13 August (backend) / 12 August (platform). That is a decision
nobody has taken, not an oversight.

---

**F4 is closed as of 2026-08-14** — built, reviewed, its three substantive findings fixed
(`2ae0445`), and `F4-AS-BUILT.md` written. It does not appear in the owed-review list below.

**~~The owed-review list is now five~~ — all five have now run (2026-08-14). The list is empty and
the backlog has moved from "unreviewed" to "unfixed".** M4.1's multi-agent gate, M5's, F3's, the
activation increment's console side, and the API reference increment: **44 findings, 2 already fixed
downstream, and 23 closed by the four fix increments that followed — roughly 19 open.** Each is
recorded under "Known open items" below, worst first, with the ones verified by hand marked as such —
the rest are as reported and want confirming before anyone acts on them.

**~~The next increment is not a feature~~ — four fix increments have now run (2026-08-14), and
23 of the 44 findings are closed.** In order: `c3411f2` and `49c87dc` for the two live defects in
published numbers (MRR normalising by a truncated reciprocal; churn counting subscription rows rather
than gyms), then **M5.2** (`267b6c4`, `7672bb6`) and **M4.2** (`e235807`, `d8edfec`, `445be2a`) on the
M4 → M4.1 precedent, plus `728157e` for all nine frontend findings and `c50fca1` for the four suites
the 03:50 sweep could sabotage. Each is described in its own entry under "Known open items".

**What the fixes could not undo.** MRR and churn are correct from now on, and the rows already written
are not: `daily_platform_stats` is write-once by design, so `/analytics/revenue` and `/analytics/churn`
step at the first nightly run after those commits, with nothing marking the boundary. Correcting the
history means either building a recompute-and-overwrite path — which the write-once rule exists to
forbid, and which M5's own backfill finding independently warns against because the population counts
are unwindowed — or accepting the step. **That is a product decision and nobody has taken it.**

**Roughly 19 findings remain, none of them severe.** The largest is the maintainer's unlocked plan
read, which is genuinely blocked rather than merely pending: three of its four callers hold no
transaction, so a `FOR UPDATE` there would release at end-of-statement and buy nothing until those
call sites are audited. Then the seeder lock's overstated guarantee, the `features: null` unit fake,
`/dashboard/health` gated behind a check that itself reads the database, the duplicated `livingGyms`
predicate, `GetSummaryAsync`'s round trips, and three API-reference items.

**Two decisions are the owner's, not the implementer's.** Whether an override records a **value** or an
**intent** — today a deny-exception ("suspended pending fraud investigation") is hard-deleted when it
happens to coincide with its plan, and the capability switches itself back on when the plan flips;
fixing that is a schema change that interacts with the deferred expiry work. And the console's `e2e`
CI job, which has never passed in eight runs because it checks out a private sibling repository with a
token scoped to one repo: it needs a PAT or GitHub App secret, so the browser suite is currently gated
by nobody.

**One pattern is worth more than any single finding.** Three separate increments shipped a fix or a
guard that could not work, each with a test that looked like coverage: `4ce45d0`'s catch is
unreachable because `UnitOfWork` translates the exception it catches; the API reference's security
assertion checked `GetArrayLength() > 0`, which `[{}]` satisfies; and M5's zero-decimal rounding test
uses JPY 12,000, a price that divides evenly and therefore cannot see the bug it guards. All three
passed CI. The lesson is not "write more tests" — it is that a test whose fixture cannot exhibit the
failure is indistinguishable from one that can, until someone checks.

**Two things F4 left behind on purpose**, both recorded in its as-built: a shared `FeatureRow` shell
is the earned extraction (the row markup is copied across all three surfaces, and the E2E journey
selects rows by their Tailwind classes, so a purely visual change to that markup silently breaks
roughly eight tests), and two design choices nobody with an eye for design has seen — `SourceBadge`'s
tone mapping, and the four "X unavailable" screens that turned red with `role="alert"` as a side
effect of the `toastApiError` migration.

**~~The activation increment's review~~ is DONE (2026-08-13), and its fixes are merged (`a74967f`).**
All five questions this section listed were answered, and three of them turned into findings:

- *Spending the token before the password is validated* — **sound, and keep it.** Consuming only on
  success leaves a link replayable for its full lifetime by anyone willing to submit a password the
  policy rejects, at no cost to them. The typo tax is the smaller harm. But note the interaction the
  review found: that path is precisely what made the concurrency defect reachable, because it commits
  the consumption with no user row in the transaction.
- *Timing on the four-way `404`* — **marginal, not worth changing.** Unknown, expired and consumed all
  return after one query; valid-but-suspended costs a second. Only somebody already holding a token
  can measure the difference, and the token is 256 bits from the OS CSPRNG.
- *Inline send after the commit* — **acceptable as built.** The send is post-commit, every failure is
  caught, and the account survives a dead mail server with the header telling the truth. The outbox
  would add durability, not correctness, and building it first is the milestone-in-front-of-a-defect
  ADR-53 exists to avoid.
- *`Email:ConsoleBaseUrl` startup validation* — **not enough, and it was already wrong.** It checks
  that the URL is absolute, not that it is right; a console under a sub-path had that path silently
  discarded from every link. Fixed.
- *`X-Invitation-Email: sent` when email is disabled* — **a defect, not a wart.** Combined with
  `LoggingEmailSender` deliberately logging no body, it produced an account nobody could activate,
  reported as an invitation delivered. There are three outcomes now: `sent`, `failed`, `disabled`.

**The console side of the same increment (`d27e32a` in GymOS-Platform) has still not been reviewed.**
This pass covered the backend only. The activation form is where the raw token is handled in a
browser, so it wants its own look: whether the token reaches the page from the query string without
leaking into history, logs, or a referrer, and what the page does with a `404` from either endpoint.

**M4's whole-increment review has now happened, and M4.1 is its fix-up** — built, verified, and
proved live on branch `m4-review-fixes`. That review is the gate M3.3 and M3.4 went through and M4
never received before merging; M4.1 closes the Critical and all six Importants it found, and this
close-out corrected the documentation the review flagged as claiming a structural property
(resolution "never expressed in SQL") the code did not have. **M4.1 now owes its own review** — the
same adversarial protocol, over the seven commits on `m4-review-fixes` plus this close-out. The
reviewer's sharpest questions here are probably: whether a day's window between a plan/default change
and the next sweep is an acceptable guarantee or should be tighter; whether the retry-once fix on
`PUT /gyms/{id}/features` could still surface a real conflict as a false convergence; and whether the
gym-detail permission gate's two false starts (into the read side, then back to Application) leave
any other read-side query with the same latent AD-3 temptation.

**M5 also still awaits its own review**, unrelated to M4.1's — over the six commits on branch
`m5-dashboard-analytics` plus its close-out. The reviewer's sharpest questions there are probably:
whether the two recorded deviations from the frozen spec (per-currency MRR instead of one summed
column; split `canceled_gyms`/`expired_gyms` instead of one `churned_gyms`) should be accepted as
written or need their own ADR; whether the population/event split for `new_gyms` (§3 of
`Analytics.md`, corrected mid-increment by an owner ruling) is the right line to have drawn; and
whether `/dashboard/summary`'s live computation is cheap enough to stay live once real gym counts
grow, or should move to the read model sooner than "when it stops being cheap."

**F3 was that next unblocked step and is now done** (`a504abb` in GymOS-Platform), so the frontend is
no longer where the slack is. Then, in priority order:

1. **F4 — the M4 feature-flags console — is now built** (see the milestone row above): the catalogue,
   the gym-detail tab, and the plan-detail card, over the M4.1 contract it was waiting on. Its review
   is now owed, the same as everything else in the next item.
2. **Five reviews are outstanding, and F4's joins the queue rather than displacing anything in it** —
   M4.1's multi-agent gate, M5's whole-branch review, F3's, the console side of the activation
   increment, and now F4's. Pick whichever order suits the reviewer; none blocks another, and the API
   reference increment below is still its own separate item in the same backlog.
3. **~~Two things F3 deliberately left owed~~ — the seed is DONE (2026-08-13); `F3-AS-BUILT.md`
   remains owed and stays owed on purpose.** The **E2E seed of `daily_platform_stats` including a
   deliberate one-day gap** now exists as `GymOS-Platform/tests/e2e/seed-analytics.sql`, applied by a
   Playwright `globalSetup` (`tests/e2e/global-setup.ts`) before the browsers start, so the gap rule
   (D3, F3's most important behaviour) is proved **end to end** and no longer in the unit suite only.
   **8/8 analytics specs pass across both viewports with zero skips.** Correcting this entry's own
   count: **two** specs skipped, not three — revenue and churn. The growth spec drives the range
   control and the dashboard spec reads live tables, so neither ever needed seeded rows.
   `F3-AS-BUILT.md` is still deliberately unwritten so it can record what the review changes rather
   than be rewritten after it — this entry previously said it was worth doing *before* the review,
   which contradicted its own reason in the same sentence.

   What the seed is, since its shape is load-bearing rather than arbitrary: 14 candidate days ending
   yesterday with **one left out** (`CURRENT_DATE - 7`) → 13 rows, one interior gap, one missing day,
   which is the sentence the specs assert. Every date is relative to `CURRENT_DATE` because hard-coded
   dates would drift out of the console's default 30-day range and quietly return the specs to
   asserting nothing. Two currencies, because against a single-currency screen a summed `Total` column
   and a per-currency column are the same column and **D2** would be untestable. The population three
   sum to `total_gyms` because Growth stacks them to that height (**D4**). `global-setup.ts` verifies
   the shape it wrote and **throws** if it is not 13-rows-one-gap, so SQL drift surfaces as one clear
   message before the browser starts. It reaches Postgres via `docker exec`, so **no new dependency** —
   the same constraint F3's charts were built under.

   Both directions were proved, per the standing rule: the specs were watched failing on the empty
   series first, and the gap assertion was then watched failing against a **deliberately continuous**
   seed (14 rows, no gap) — so it is known to be load-bearing on the gap and not merely green.
4. **The API reference increment awaits review** (Konstantinos's) — unchanged by this work, and the
   only thing left over from before the audit's scope.
5. **Audit finding 7** wants its own increment: concurrent failed sign-ins collide on the user row's
   version, so `/auth/login` can answer 409 and lockout under-counts. Small domain change, listed in
   full under "Known open items".
6. **M6** — per the approved roadmap (monitoring, settings). `GET /monitoring/*` (§3.7) and the device
   registry it needs are M6's; M5 deliberately left them out (`Analytics.md` §10).

Nothing in M4.1 or M5 changed the standing debts: **R14 (RLS) has still never run** and is owed with
the first tenant-scoped entity, and the Members domain remains frozen and ready to implement. M5's
own platform stats did **not** end up needing `Money` currency conversion (AD-43) — the owner's ruling
to report MRR per currency rather than summed avoided that dependency rather than building it. M4.1's
own deferrals (override expiry, the four-subquery plan projection, tracked catalogue reads, the
log-count conflation) are recorded in `docs/BusinessRules/Features.md`'s deferred-list section and
above, not repeated here.

**R14's mechanism is now built and tested, and the database-roles close-out means it actually
filters** — see "R14 as built" and "Database roles (R14 close-out) as built" above, and
`docs/BusinessRules/TenantIsolation.md`. **No production table is tenant-scoped by it; `members` is the
first, and it is next.** Nothing structural blocks starting Members. One thing that used to sit in this
paragraph is now closed — the platform no longer connects as a superuser, it connects as `gymos_app`,
which owns nothing — and one thing still wants the owner's attention rather than surprising whoever
builds Members: there is still no way to issue a **gym-scoped** grant through the API at all, which sits
squarely inside Members' own scope and is the real reason the tenant-resolution middleware has never
been exercised against a real grant.

## Known open items / honest notes

### From the environment model (2026-08-24) — open

- **Demo data is not implemented, and `populate-all.ps1` says so rather than exiting 0 quietly.**
  Identity, permissions, the feature catalogue and the SuperAdmin are seeded and usable (verified:
  `1/5/16/12`), but there are **zero gyms and zero plans**, so the dashboards and the consoles have
  nothing to show. It has to be a seeder *inside the application*: every object is an aggregate with
  invariants in code (`Gym.Provision`, `SubscriptionPlan.Create`, `GymSubscription.Assign`,
  `Invoice.Issue`, `DailyPlatformStats.Record`) and invoice numbers come from a stored procedure the
  app calls, so INSERTing past all that would produce rows the domain considers impossible. The
  mechanism is the existing host-mode pattern — a `Seed:DemoData` flag alongside
  `Database:MigrateOnly`. **Note the domain limit**: Members, trainers, classes, bookings, check-ins
  and payments do **not exist in code** (Members is a frozen design document), so no seeder can create
  them and none should pretend to.

- **The unit suites and the build ARE verified; the integration suite is not.** The local SDK gap
  (pin 10.0.302, installed 10.0.301) was worked around by running the toolchain **inside the
  `mcr.microsoft.com/dotnet/sdk:10.0` container** against the mounted repository — that image carries
  **10.0.400**, which satisfies `rollForward: latestFeature`. Anyone stuck without a local SDK can do
  the same:

  ```
  docker run --rm -v "/c/Personal Projects/GymOS/GymOS-Backend:/src" -w /src \
      mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/GymOS.ArchitectureTests
  ```

  Measured this way on the PINNED SDK 10.0.302: **build 0 warnings / 0 errors**, **`dotnet format` exit 0**, and
  **1,137 unit tests passed, 0 failed** — SharedKernel 131 · BuildingBlocks 74 · Domain 434 ·
  Application 247 · Infrastructure **98** (was 67; the 31 new `DatabaseEnvironmentGuardTests` cases
  are the difference) · Architecture 153.

  **Pin the image tag to `10.0.302`, not `10.0`, and this is the important part.** The floating `10.0`
  tag currently carries **10.0.400** — a higher feature band, which `rollForward: latestFeature`
  happily accepts. It builds and tests identically, but **`dotnet format --verify-no-changes` disagrees
  with itself across the two bands**: on 10.0.400 it reports ~150 WHITESPACE errors across files nobody
  had touched, and on 10.0.302 the same tree is **clean, exit 0**. Measured on both, and on
  pre-migration `development` too, which fails on 10.0.400 in exactly the same way — so it is the
  toolchain disagreeing, not the code. Anyone who "fixes" those errors will be reformatting the whole
  repository to satisfy an SDK CI does not use.

  ```
  docker run --rm -v "/c/Personal Projects/GymOS/GymOS-Backend:/src" -w /src \
      mcr.microsoft.com/dotnet/sdk:10.0.302 dotnet format GymOS.Backend.slnx --verify-no-changes
  ```

  The same applies to installing an SDK locally: **winget offers 10.0.400**, which will build fine and
  then disagree with CI about formatting. Install 10.0.302, or expect that.

  **One other trap.** Passing `--artifacts-path /tmp/art` puts the test assemblies outside the
  repository, and `SolutionLocator` walks up from the assembly to find `GymOS.Backend.slnx` — so 8
  architecture tests fail with a `TypeInitializationException` that has nothing to do with the code.
  Run them with default output paths.

- **The integration suite (314 tests) has still not been run.** It needs Testcontainers, which needs a
  Docker socket from inside the SDK container, and that was not judged worth the setup here. The paths
  it would cover were instead verified by hand against a live stack — migrations, seeding, the
  environment guard in both directions, and a working sign-in (see the environment-model section) —
  but that is not a substitute. **Run it before promoting anything.**

- **The deploy workflows have never run.** See the environment-model section above. Still required:
  choose a hosting target, provision it plus two Azure SQL databases in an EU region, create the two
  GitHub Environments and their secrets, implement the `deploy` step, flip the enable variables.

- **Only this repository's CI lost the `staging` trigger.** The other six GymOS repositories still
  build it. Left alone deliberately rather than editing six repos nobody asked about.

### From the SQL Server migration (2026-08-24) — all open, none merged

- ~~**`appsettings.Development.json` was never migrated, so the host-run API does not start.**~~
  **Fixed 2026-08-24.** It read
  `Host=localhost;Port=5432;Database=gymos;Username=gymos_app;Password=gymos_dev_app_password`.
  `Microsoft.Data.SqlClient` rejects `Host` and `Username` as unsupported keywords, so
  `dotnet run --project src/Host/GymOS.WebApi` and Visual Studio F5 on the API project both fail at
  startup. **Nothing caught it because nothing exercises that file:** the integration suite builds its
  own strings in `SqlServerFixture`, and the containerised API takes its own from
  `docker-compose.override.yml` — so the author's "the composed stack boots and the seeded administrator
  signs in" is true and still leaves this broken. The intended value, matching the container's own
  app-principal string:
  `Server=localhost,1433;Database=gymos;User Id=gymos_app;Password=Gymos_dev_app_password1;Encrypt=True;TrustServerCertificate=True`.
  **Found by inspection, not by running it** — the SDK pin (10.0.302 vs the 10.0.301 installed) prevented
  building. Left unfixed deliberately: it is the branch author's file and his call.

- **ADR-54 is `Proposed` and its rationale is a placeholder.** The architecture is frozen and names
  PostgreSQL across all four specification documents, so this implementation ran ahead of the freeze
  protocol rather than behind it. The ADR is written and specific about what it supersedes; what it
  cannot supply is *why*, because nothing in the branch says. **Until that paragraph is written by the
  author and approved, the migration is unauthorised work, not merely unmerged work.**

- **`EnableRetryOnFailure` is off, and Azure SQL is the production target.** Deliberate and documented
  (`PlatformInfrastructureServiceCollectionExtensions.cs:42`): `SqlServerRetryingExecutionStrategy`
  refuses user-initiated transactions, and both `PlatformIdentitySeeder` and `FeatureCatalogSeeder` open
  one on every boot, so switching it on fails startup rather than hardening it. But
  `ARCHITECTURE-ADDENDUM.md`'s resilience table **mandates** bounded transient retry, and on Azure SQL
  transient faults are routine rather than exceptional — so this is a real gap the migration opened, not
  a formality. Closing it means making every `BeginTransactionAsync` delegate replayable under
  `Database.CreateExecutionStrategy().ExecuteAsync(…)`, which changes transaction semantics. Tracked as
  the first follow-up in `docs/runbooks/managed-sqlserver-roles.md`; **it should carry a milestone rather
  than a runbook note.**

- **Every primary key is now a clustered index over a random GUID.** `id uuid PRIMARY KEY` cost nothing
  under PostgreSQL's heap tables; EF Core makes a SQL Server primary key clustered, so each insert lands
  at a random point in the table's physical order and splits pages. Nothing in the migration addresses
  or mentions it. The remedies are ordinary — a non-clustered primary key over a clustered surrogate, or
  a lower fill factor — but they are schema decisions, so they belong in a planned increment and not in a
  hotfix once `audit_logs` and `outbox_messages` are large.

- **Three approved future schemas can no longer be built as written**, and each will surface at its own
  milestone unless someone plans for it now. `ARCHITECTURE-PLATFORM-CAPABILITIES.md` §5's search v1 is
  `tsvector` + `pg_trgm` + GIN indexes; SQL Server Full-Text Search covers ranked full-text but **typo
  tolerance does not survive the move**. `rule_versions.granted_permissions` is a `text[]`, and SQL
  Server has no array type — it becomes a junction table or JSON when Automation is built. And `jsonb`
  is now `nvarchar(max)`, which is **not indexable the way a GIN index was**: settings resolution,
  automation conditions and gateway config each need a persisted computed column and an index on that.
  All three are recorded in ADR-54; the search one is the largest and the easiest to walk into.

- **The fifth engine difference is undocumented.** The database-wide `Latin1_General_100_CI_AS` collation
  makes the **client-supplied `idempotency_key` match case-insensitively**, where the PostgreSQL cluster's
  `lc-collate=C` compared it byte-for-byte. Two keys differing only in case now collide. Negligible in
  practice for random keys, and the collation itself is deliberate, asserted by a test, and what now
  provides §2's `citext` slug semantics — but the other four differences got written up where they bite
  and this one did not. Token hashes are unaffected: the server computes lower-case hex on both sides.

- **`IdempotencyRecordConfiguration`'s XML doc is wrong in both its claims.** It says the unique claim
  index needs `COALESCE` over the nullable `tenant_id` and lives in the `AddIdempotencyRecords`
  migration. The sentinel was dropped (SQL Server treats NULLs as equal in a unique index, so the
  collision the `COALESCE` manufactured is the default) and that migration is deleted. The accurate
  account is in `20260823193131_RuntimeDatabaseProgrammability.cs:113`.

- **`docs/Infrastructure/DOCKER.md` contradicts itself about Mailpit.** Its header lists five services
  running today including `mailpit`, and `docker-compose.override.yml` states explicitly that Mailpit has
  no profile since ADR-53 — but the §1 architecture diagram still lists it under "profiles (off by
  default)". The compose file is correct; the diagram is stale.

- **`docs/runbooks/managed-sqlserver-roles.md` has never been applied against a real Azure SQL
  instance.** The same was true of the PostgreSQL runbook it replaces, so this is not a regression — but
  the new one carries more weight, because Azure SQL has no `sa` and no server-scoped `CREATE LOGIN`, so
  the initdb scripts **do not run there unchanged**. First real provisioning will be the first test of it.

- ~~**Two documentation items parked at the end of the database-roles increment**, both raised by its
  final re-review, both correctness-neutral and neither fixed because the increment's fix budget was
  spent.~~ **Both taken 2026-08-15, at the start of the next piece of work as this entry asked.**
  Build 0 warnings, `dotnet format` exit 0; neither change touches executable code.
  - `DatabaseReadinessHealthCheck`'s remark said a host pointed at a **nonexistent** database "would
    have reported ready". It would not have — the old `AddDbContextCheck` called `CanConnectAsync`,
    which opens a connection, so `3D000` arrived as a failed open that EF's `RelationalDatabaseCreator`
    catches into `false`, and that case already reported Unhealthy. The remark now says what the check
    actually adds — the database that **exists but is empty or behind** — keeps the measured
    nonexistent-database experiment as its own paragraph, and records the correction in place rather
    than quietly deleting the wrong sentence. **Read off EF's implementation, not re-observed**, and
    the remark says so: Docker was not running and standing the stack up to re-measure a comment was
    not judged proportionate. Anyone who does observe it should strike that caveat.
  - `docs/runbooks/managed-postgres-roles.md` verified `datdba` but not `proowner`. "Verifying before
    you trust it" now checks `pg_get_userbyid(proowner)` and `prosecdef` for both `SECURITY DEFINER`
    functions, gives the `ALTER FUNCTION … OWNER TO gymos_migrator` repair, and names the ordering that
    causes it: Step 2's `REASSIGN OWNED` covers functions that already exist, so the exposure is an
    administrator applying the migration **after** the ownership transfer. Blast radius stated as
    bounded, and re-checked against `SecureRuntimeDdlFunctions` rather than taken from the review —
    both functions take only an `integer`/`date` and escape every interpolation with `%I`/`%L`, so no
    arbitrary DDL is reachable; what is reachable is the fixed DDL they already issue, as a superuser.

- ~~**BLOCKS MEMBERS, AND MUST BE ITS FIRST TASK: `PostgresFixture` has to move to the
  migrator/app shape for the whole integration suite.**~~ **Done 2026-08-15 (`7a75dc5`), suite green at
  313.** The fixture now builds `gymos_test_migrator` and `gymos_test_app`, applies the deployment's
  `ALTER DEFAULT PRIVILEGES` grants and the `EXECUTE` grants on the two `SECURITY DEFINER` functions,
  and migrates each database as the migrator before handing back an app connection string;
  `AuthenticationHost` no longer migrates. **What it surfaced is the part worth keeping: nine suites
  were doing their own DDL and only worked because one role owned everything** — eight migrated
  themselves redundantly, `OutboxCaptureTests` calls `EnsureCreated`, `AuditLogPartitioningTests`
  detaches a partition. Each now uses the migrator explicitly. Three consequences were changes rather
  than breakage: a fresh database is no longer unmigrated, so the two tests needing that state ask for
  it by name (`CreateUnmigratedDatabaseAsync`); `MigrateOnly` now runs as the migrator against an empty
  database, which is the arrangement it exists for; and pooled connections are returned after 15s
  instead of Npgsql's default 300s, because the cap bounds each pool while nothing bounds the number of
  pools and migrating every database pushed the run past `max_connections`. The original reasoning,
  kept because it is why this had to come first: `TenantIsolationValidator` builds its table list from
  `_dbContext.Model`, so the check is **model-driven, not usage-driven**: the moment any production
  entity implements `ITenantScoped`, `TenantScopedTables()` is non-empty in **every** host that
  builds the model — all ~312 integration tests' worth — whether or not that suite ever reads the
  table. Those hosts connect as `PostgresFixture`'s single role, which owns the tables it migrated,
  so the ownership check (`pg_has_role(current_user, c.relowner, 'USAGE')`) reports a problem and the
  validator refuses **every** boot. Nothing is broken today: no production entity implements the
  marker, and the tenancy suite's probe entity is registered only in that suite's own host. The
  database-roles design's §6 stated the rule as "any suite that *touches* a tenant-scoped table uses
  the two-role shape", which is wrong for exactly this reason; §6 now carries a correction. Adding
  `Member` without doing this first produces ~300 simultaneous boot failures whose cause is nowhere
  near the change that triggered them.

- ~~**The platform's PostgreSQL connection is a superuser, which voids `FORCE ROW LEVEL SECURITY`
  entirely.**~~ **Closed by the database-roles increment (2026-08-15) — see "Database roles (R14
  close-out) as built", above.** The API now connects as `gymos_app` (`NOSUPERUSER NOBYPASSRLS`, owns
  nothing) and the migrator connects as `gymos_migrator` (same attributes, owns everything and runs
  DDL); neither is `gymos_admin`, the renamed superuser (`POSTGRES_USER`) reserved for humans and
  maintenance. `TenantIsolationValidator`'s role check is now unconditional rather than gated on
  `ITenantScoped`, so it runs — and would refuse the boot — on every start, not only the day `members`
  lands. Reasoning in `docs/BusinessRules/TenantIsolation.md`.

- **A gym-scoped grant cannot be issued through the API at all (R14 close-out) — and a 2026-08-15
  design pass found that issuing one today would be a privilege-escalation defect, not a gap.**
  `SetUserRolesCommandHandler` rejects any attempt at a non-platform-scoped role and only ever writes
  `UserRole.TenantId = null`, so every grant any role holds today is platform-wide. That is the real
  reason `TenantResolutionMiddleware`'s grant check — built and integration-tested in this increment —
  has never had a gym-scoped grant to check against; the existing suite is entirely platform-scope
  callers, which is also why the middleware is provably inert against it.
  **Three findings, all read from the code rather than reasoned about:** (1)
  `IdentityQueries.GetEffectivePermissionsAsync` filters on `UserId` alone and `UserSecuritySnapshot`
  carries a flat permission list with no tenant, so a `UserRole` row with a `TenantId` would confer its
  role's permissions on **every** route, platform ones included; (2) `TenantResolutionMiddleware` runs
  *after* `UseAuthorization()`, so the endpoint policy is evaluated before any tenant is resolved while
  `AuthorizationBehavior` runs after it — making the evaluator tenant-aware breaks M1.3's "both layers
  read the same evaluator, so they cannot disagree" unless the ordering changes first; (3) no
  tenant-scoped `Role` can exist at all, because the only `Role.Create` call in `src/` is
  `PlatformIdentitySeeder`, always `RoleScope.Platform` — a grant endpoint would have nothing to grant.
  **Designed and approved, deliberately not built:**
  `docs/superpowers/specs/2026-08-15-gym-scoped-grants-design.md`. Ten decisions, chief among them that
  tenant resolution moves between `UseAuthentication()` and `UseAuthorization()` (its current placement
  is justified by a reason that does not hold — reading grants needs the authenticated *identity*, which
  authentication already supplies), that effective permissions become the union of platform-wide grants
  and grants naming the acting tenant, and that per-gym roles are provisioned with the gym. The build
  order is chosen so the escalation window never opens: ordering, then evaluation, then issuance.
  **The Members increment builds it** — it brings the first `ITenantScoped` entity and is the only
  increment that can prove it end to end. One claim in the spec is marked as taken from framework
  documentation rather than observed (that `WebApplication` inserts `UseRouting` at the head of the
  pipeline, so route values exist at authentication time); step 1 must confirm it before the reorder is
  trusted.

- ~~**`TenantScope` is owed, not built (R14 close-out).**~~ **Built 2026-08-15**, TDD, six unit tests in
  `GymOS.BuildingBlocks.UnitTests`; build 0 warnings, `dotnet format` exit 0.
  `BuildingBlocks.Application/Tenancy/TenantScope.cs`.
  **Measured, every project run individually to completion with no `Aborted`:** SharedKernel 131 ·
  BuildingBlocks **72** (was 66 — the six new tests) · Domain 434 · Application 247 · Infrastructure 67
  · Architecture 153 = **1104**. **The 312 integration tests were not run** — Docker Desktop was not
  running on this machine — so the full-suite figure is not re-measured here; nothing in this increment
  touches a path they cover, but that is reasoning, not evidence.
  **The design's §4 could not be built as written, and that is the finding worth keeping.** §4 says the
  helper "sets the tenant for one gym and clears it on dispose". There is nothing to clear with:
  `TenantContext.SetTenant` refuses a re-point and exposes no reset, deliberately — a second set would
  mean any query that already ran did so under the previous value, which the caller cannot see and the
  database will not report. A clear would reopen exactly that hole. So `TenantScope` **owns an
  `AsyncServiceScope` per gym** — `For(gymId, scopeFactory)` creates the scope, sets the tenant on
  *that* scope's fresh `TenantContext`, exposes `ServiceProvider`, and disposes the scope on dispose.
  "Clear on dispose" becomes "the scope holding the tenant ceases to exist": same guarantee, no reset
  path. `TenantIsolation.md` carries the correction; the design spec stays as the historical record.
  **It has no production caller, on purpose.** `FeatureOverrideSweepJob` has the scope-per-gym shape but
  sets no tenant — its tables are platform-scope — so routing it through this would be wrong, not a
  cleanup. Members brings the first `ITenantScoped` entity and the first job with tenant data to loop.
  **So what is proved is the scoping contract, not isolation**: the end-to-end test that RLS filters
  through a `TenantScope` needs a tenant-scoped table and belongs to that increment. Two of the six
  tests are regression guards that pass on arrival and fail if anyone reintroduces the set-and-clear
  shape; the successive-gyms guard was proved able to fail by making `For` reuse one cached scope —
  the exact mistake `TenantIsolation.md` names — and watching it break.
  **Residual risk, stated rather than solved:** a job that resolves services from its *own* scope
  instead of `scope.ServiceProvider` silently carries the job's tenant, and nothing in the type can
  detect it. `ServiceProvider` being the only way to reach anything is the whole mitigation.

- **⚠️ THE CHURN COUNT IS FIXED IN CODE (2026-08-14) — SAME STORED-HISTORY CAVEAT AS MRR BELOW.**
  `canceled_gyms` and `expired_gyms` counted `GymSubscription` **rows**, not gyms, so a gym that
  churned twice in a day was two churned gyms. Reproduced first as a failing integration test
  (`AGymThatChurnsTwiceInADayIsOneChurnedGym`: assign → cancel immediately → assign → cancel again,
  expected 1, got 2) and now fixed with a `Distinct` on `GymId`. The premise the reviewer identified
  is real and is asserted by the test itself: the one-live-per-gym unique index is **partial**
  (`status IN (1, 2, 3)`), so a cancelled row leaves the constraint behind and the next assignment
  inserts a fresh one — terminal rows accumulate per gym without limit.
  **Deliberately not deduplicated across the two counts**: a gym that cancels one subscription and has
  another expire on the same day churned for two different reasons, and keeping those causes apart is
  the entire point of M5 refusing the frozen spec's single `churned_gyms` column (D5).
  **Stored rows are not corrected**, for the same write-once reason as MRR: any day already compiled
  keeps its inflated churn figure, so `/analytics/churn` steps at the first nightly run after this fix.
  On a small platform the old figure could exceed 100%.
  **Still open on this same code path, and not fixed here:** M5's third finding, that the Expired count
  keys on `CurrentPeriodEndUtc` — a business date `Expire()` never updates — so an expiry applied after
  its own day was compiled lands on no day at all. That needs an `ExpiredAtUtc` mirroring
  `CanceledAtUtc`, which is a schema change and a separate increment. It is dormant today only because
  `DisableAutoRenew` has no caller outside tests.

- **⚠️ THE MRR BUG IS FIXED IN CODE (2026-08-14) BUT THE STORED HISTORY IS STILL WRONG, PERMANENTLY.**
  `Money.Divide(decimal, RoundingPolicy)` was added — its absence is why the reciprocal was written in
  the first place — and `MonthlyRecurringRevenue.PerMonth` now divides. `/dashboard/summary` is correct
  from now on, because it computes live on every request.
  **`daily_platform_stats` is not.** Every row already written for a day whose platform included a
  yearly subscription at an affected price carries the cent-low figure, and the table is **write-once
  by design** — `PlatformStatsJob` refuses a day that already has a row, and there is no update path.
  So `/analytics/revenue` will serve a discontinuity: correct from the first nightly run after this
  fix, understated before it, with no marker on the boundary. Correcting it means either a deliberate
  recompute-and-overwrite path (which the write-once rule exists to forbid — see the backfill finding
  below) or accepting the step. **That is a product decision, not a code one, and nobody has taken
  it.** The one mercy is scale: only Yearly and Quarterly cycles divide at all, and only about 1 price
  in 24 is affected, so the error is small, one-directional, and bounded by how many annual plans were
  live on each day.

- **M5.2 (2026-08-14) fixed six of M5's fifteen**, across `c3411f2`, `49c87dc`, `267b6c4` and
  `7672bb6`. Closed: the MRR reciprocal (see below); churn counting subscription rows rather than gyms;
  the population read non-atomically, now one `GROUP BY` with `DailyPlatformStats.Record` refusing a
  population that does not reconcile; `CompileAsync` accepting a day that has not ended, and the class
  doc that invited the backfill four other documents forbid; the unordered `mrr` projection, sorted in
  memory because ordering by a `Money` complex-type member does not translate and returns a 500; and
  the `Expired` count keying on a date `Expire` never stamped — there is an `ExpiredAtUtc` now, with
  partial indexes on it and on `canceled_at_utc`, which had none at all.

  **Still open from that fifteen:** `/dashboard/health` gated behind a permission check that itself
  reads the database, so with Postgres down the panel 500s instead of reporting Unhealthy; the
  `livingGyms` predicate and the billing-contributions query duplicated verbatim between the live
  query and the nightly compiler; `GetSummaryAsync`'s six sequential round trips materialising every
  paying subscription per poll; the `Mrr` navigation's order-dependent `FindNavigation(...)!`; and
  three tests that assert less than their names claim — deleting the three `AnalyticsRead` grants
  leaves every M5 test green.

- **M5's multi-agent gate has now run (2026-08-14) — 15 findings, and the first one is a money bug in
  the platform's headline number. VERIFIED BY HAND, not taken on the reviewer's word.**

  `MonthlyRecurringRevenue.PerMonth` normalises an annual price with
  `Price.Multiply(1m / months, RoundingPolicy.HalfEven)` — it **multiplies by the truncated
  reciprocal instead of dividing.** `1m/12` cannot be represented exactly, so every product that
  should land exactly on a half-cent lands just below it, and half-even then rounds *down*. An
  exhaustive scan of two-decimal yearly prices from 0.01 to 2000.00 was run against `System.Decimal`
  to check this: **8,327 of 200,000 prices differ, and all 8,327 are low. None are high.** A
  systematic one-directional shortfall, not rounding noise. EUR 599.94/yr reports 49.99 where
  599.94/12 = 49.995 → 50.00. Zero-decimal currencies are worse: JPY 59,994/yr reports 4,999 instead
  of 5,000.

  The wrong figure is served live by `/dashboard/summary` **and written permanently into
  `daily_platform_stats` by the write-once nightly job**, where nothing can recompute it. `Money` has
  no `Divide` — only `Allocate` — which is how the reciprocal got written in the first place.
  `AZeroDecimalCurrencyRoundsToWholeUnits` exists and passes because its fixture is JPY 12,000, an
  exact multiple of 12; a price that divides evenly cannot see this bug.

  **The rest, worst first, reported and not yet verified.** `canceled_gyms`/`expired_gyms` are named,
  stored and published as *gym* counts but computed as `GymSubscription` **row** counts with no
  `Distinct` — one gym that churns twice in a day is two churned gyms, and with a single tenant
  `/analytics/churn` can return a churn rate above 100%, permanently. The Expired count keys on
  `CurrentPeriodEndUtc`, a business date `Expire()` never updates, so an expiry applied after its own
  day was compiled is counted on **no** day at all (dormant only because `DisableAutoRenew` has no
  caller yet — which is exactly why an `ExpiredAtUtc` mirroring `CanceledAtUtc` is cheap now). The
  eight reads that build one stats row run in **no transaction**, so a gym created mid-compile
  produces a stored row where `total_gyms ≠ active + suspended + provisioning` — the identity
  `Analytics.md` explicitly promises — and write-once means it is served that way forever.
  `CompileAsync` validates `statDate` not at all while its class doc *invites* hand-triggered
  backfill that four other documents forbid; population counts are unwindowed, so backfilling a gap
  writes today's counts under an old date and the write-once guard then blocks any correction.
  `/dashboard/health` is gated on a permission check that itself reads the database, and the database
  is the only registered check — so with Postgres down the panel returns a bare 500 instead of the
  "Unhealthy" it exists to report. Plus: `mrr` is projected with no `OrderBy` so `/analytics/revenue`
  and `/dashboard/summary` can disagree on currency order; the `livingGyms` predicate and the whole
  billing-contributions query are duplicated verbatim between the live query and the nightly
  compiler (only the calculator was extracted); `GetSummaryAsync` costs six sequential round trips
  and materialises every paying subscription per poll; `canceled_at_utc` is in no index at all; and
  three tests assert less than their names claim — deleting the three `AnalyticsRead` grants this
  increment adds leaves every M5 test green.

- **M4.2 (2026-08-14) fixed six of M4.1's fifteen**, across `e235807`, `d8edfec` and `445be2a`.
  Closed: the sweep's missing transaction (it now opens one and reads the deciding row under a lock,
  via a new `FindLiveByGymForUpdateAsync`); the unreachable catch; the override DELETEs staged inside
  the billing transaction — they run **after** the commit now, so a support agent editing a feature
  flag can no longer roll back a renewal and burn the invoice number it had already drawn from a
  sequence that never gives numbers back; the retry catching the wrong exception type, which made
  "the later decision wins" false for restate and clear, the operations an operator repeats most; the
  two `features` request contracts disagreeing, whose guards also moved into the handlers so they stop
  preempting the 404 and 428 and start covering non-HTTP callers; and the plan-row lock that
  serialised every gym on a plan, now `FOR SHARE` — which still blocks plan edits but not other gyms.
  The sweep's summary reports deferrals and failures too, so a pass where gyms failed stops reading
  healthy.

  **Still open from that fifteen:** the maintainer's unlocked plan read (three of its four callers
  hold no transaction, so a lock there buys nothing until those call sites are audited — a real
  change, not a one-liner); the seeder lock's overstated guarantee and `SuperAdminSeeder`'s
  unprotected check-then-act; the unit fake returning a `features: null` production cannot produce,
  which leaves the new `features.read` gate unexercised on all six write routes; `DiscardTrackedChanges`
  mapping to a scope-wide `ChangeTracker.Clear()`; and the deny-exception below, which is a product
  decision rather than a defect.

- **M4.1's multi-agent gate has now run (2026-08-14) — 15 findings, none fixed at the time of
  writing, and it is the most serious review this project has had.** Two were verified by hand before being written down here;
  the rest are recorded as reported and want confirming before anyone acts on them.

  **Verified.** *(a)* `FeatureOverrideSweepJob` contains **no `BeginTransactionAsync` at all**, while
  its own comment promises "one scope and one transaction per gym". The live-plan read, the
  maintainer's reads and the DELETE run in separate implicit transactions under READ COMMITTED, so a
  subscription upgrade committing mid-sweep lets a verdict computed against the old plan hard-delete
  an exception that is not redundant under the new one — silently, because the override rows' `xmin`
  never saw the subscription change. *(b)* The `4ce45d0` fix is dead code; see the reopened entry
  further down.

  **Reported, not yet verified, worst first.** Staging the override DELETEs inside the billing
  transaction couples renewal to an unlocked write surface: a concurrent override edit rolls the
  whole renewal back **after** `IssueForPeriodAsync` has drawn an invoice number from a sequence that
  never gives numbers back, so every racing retry burns another. Redundancy is a stateless value
  comparison, so a **deny**-exception ("suspended pending fraud investigation") that merely coincides
  with its plan for a while is hard-deleted — and when the plan flips back the capability the
  operator switched off switches on again, with no record that anyone ever intended otherwise; the
  docs argue the design only in the grant direction. `FeatureExceptionMaintainer` reads the plan with
  the **unlocked** `FindByIdAsync` and deletes from that read, though this same increment added
  `FindByIdForUpdateAsync` precisely because that read races `PUT /plans/{id}/features` — and three
  of its four synchronous callers open no transaction, so the lock could not apply there anyway.
  `PUT /gyms/{id}/features` takes an exclusive `FOR UPDATE` on the shared **plan** row and holds it
  through the catalogue read-back, serialising every gym on that plan. The new retry catches
  `UniquenessConflictException` but the reachable race throws `ConcurrencyConflictException`, so the
  documented "the later decision wins" is still false and the retry only ever helps subscription-less
  gyms. `SeedingLocks` documents a whole-startup guarantee that `pg_advisory_xact_lock` cannot give,
  and `SuperAdminSeeder` — a check-then-act insert under a unique index — got neither the lock nor
  the `IUnitOfWork` save. Plus: the two `features` request contracts now disagree (the plan route
  refuses a missing field, the gym route coerces it to empty while its own OpenAPI marks it
  required); four integration suites that assert on override rows leave `Jobs:Enabled` true with a
  new 03:50 sweep registered; a unit fake returns a `features: null` production can never produce,
  leaving the new `features.read` gate unexercised on all six write routes; and `DiscardTrackedChanges`
  maps to a scope-wide `ChangeTracker.Clear()` on a shared context that `AuditTrail` stages rows into.

- **The API reference increment's review has run (2026-08-14) — 5 findings, 2 already fixed.** Its
  Critical was that `new OpenApiSecuritySchemeReference(BearerSchemeName)` was built with no host
  document, so all 43 "secured" operations serialised as `"security": [ {} ]` — an empty requirement
  object, which is how OpenAPI spells *authentication is optional*, the exact opposite of the intent.
  The test that should have caught it asserted `security.GetArrayLength() > 0`, which `[{}]` satisfies.
  **Both were fixed downstream by `02bca0e`**, which found the same root cause independently. Still
  standing: `AddGymOSApiReferenceBrowser` reads `DOTNET_RUNNING_IN_CONTAINER` from the process with no
  seam, so its test fails the first time CI runs in a container (it passes today only because the
  workflow uses bare `ubuntu-latest`); a method doc claims three guards make the launcher impossible
  to misfire when `OpenApi:LaunchBrowser=true` bypasses all three and would open a 404 in Production;
  and `Process.Start` returning null still logs "API reference opened".

- **All nine frontend findings are fixed (2026-08-14, `728157e` in GymOS-Platform).** The activation
  console's five and F3's four, in one increment. The sharpest three were user-facing: a valid
  invitation was declared dead by any `ApiError` — including the `ApiError(0, …)` the client wraps a
  network failure in, and the 429 from a limiter shared with sign-in, so a shared office IP was enough;
  "This link has now been used" was appended to failures where the backend never touched the token; and
  the password form mirrored only the length half of `PasswordPolicy`, so a pasted trailing space burned
  a one-shot link on a mistake the form could catch. Also: the send endpoints' `X-Invitation-Email`
  header is read now, so an operator is told `sent` / `failed` / `disabled` rather than "sent" on any
  204 — the client can return a named header from a 204, and an absent one reads as "cannot confirm".
  F3's root redirect resolves the landing route (a bookmark to `/` dropped a `gyms.read`-only operator
  on a permission notice); a single recorded day is visible in the stacked area again; whole-number
  counts get whole-number ticks, and `linearScale` went from no tests to nine; and the range error says
  one thing once. The two entries below are kept for the reasoning.

- **F3's review has now happened (2026-08-14) — four findings, ~~none yet fixed~~ all fixed, see
  above.** All in
  GymOS-Platform. **(1) Medium: the root redirect bypasses the permission-aware landing resolver.**
  F3 moved `LANDING_ROUTE` to `/dashboard` and added `resolveLandingRoute` / `FALLBACK_LANDING_ROUTE`
  precisely so a caller holding neither `analytics.read` nor `audit.read` is not dropped on an empty
  screen — but only `login/page.tsx` was updated. `src/app/page.tsx` still redirects unconditionally,
  and `(admin)/layout.tsx` guards authentication only, so its docstring claiming it "guards the
  landing route itself" is false. An operator with only `gyms.read` who opens the app at `/` — a
  bookmark, or typing the domain — lands on a permission error where they used to land on `/gyms`.
  The dashboard's own comment says that state is "reachable only if the landing-route fallback is
  bypassed by typing the URL"; the app root reaches it too. **(2) Medium: a single-day contiguous run
  renders as nothing** in `stacked-area-chart.tsx`. `toContiguousRuns` deliberately yields runs of
  length 1 and `line-chart.tsx` handles that with a fallback marker; the stacked area has no such
  fallback and emits a zero-area path stroked in `var(--card)` — invisible. A range where the stats
  job ran only on isolated nights, which is the exact case D3 exists for, draws bare axes while the
  table lists the rows. **(3) Low: fractional y-axis ticks for integer counts** — a max of 5 labels
  the axis 0, 1.25, 2.5, 3.75, 5. Small counts are the normal case on a young platform. **(4) Low:
  the range error duplicates itself** — "A range covers at most 366 days; this one covers 517. A
  range covers at most 366 days."

- **The activation increment's console side has now been reviewed (2026-08-14) — five findings,
  ~~none yet fixed~~ all fixed, see above.** All in `GymOS-Platform`, and the first two are the same defect twice. **(1) The link
  check treats every `ApiError` as "link unusable"**, but the client wraps network failures as
  `ApiError(0, …)` and the endpoint sits behind a 10-per-minute per-IP limiter shared with sign-in.
  A shared office IP or one dropped request permanently tells a valid invitee their link is dead. The
  comment above the code states the opposite intent, and `providers.tsx` already uses the correct
  `status === 0` discriminator. **(2) Submit failure appends "This link has now been used" to every
  `ApiError`**, including status 0 and 429 where the backend never consumed the token — producing
  "The server could not be reached… This link has now been used." **(3) Client validation mirrors
  only the length half of `PasswordPolicy`**; the server also rejects leading/trailing whitespace and
  consumes the token *before* judging the password, so a pasted trailing space burns a one-shot link.
  **(4) The "has been sent to <email>" toasts fire on any 204**, discarding the `X-Invitation-Email`
  header that reports `sent`/`failed`/`disabled` — with SMTP down the operator is told it worked while
  the previous link has genuinely been invalidated. The invite dialog deliberately avoids that claim.
  **(5) Low: `api.get` was widened for one anonymous call with no test**, in a repo whose
  `endpoints.test.ts` exists to pin exactly that.

- **The owed-review ledger was wrong, corrected 2026-08-14.** M4.1 and M5 were both listed as
  "review owed" when both had already had a whole-branch review — `f0d8b94` ("M4.1 whole-branch
  review fixes") and `b4d95b4` ("Apply M5 whole-branch review fixes"). What they actually owe is the
  heavier **multi-agent whole-increment gate**, the one M4 received. Two tiers exist and this file
  used one phrase for both, which read as five never-reviewed increments when three is the true
  number.

- **GymOS-Platform's `e2e` CI job has never passed — 8 runs, 8 failures, since F1.** It checks out
  `GymOS-Backend`, a **private** repo, with the default `GITHUB_TOKEN`, which is scoped to the
  current repository; GitHub answers `Not Found` rather than a permission error. Fixing it needs a
  PAT or GitHub App token with read access to both repositories, stored as a secret — a credential
  decision, not a code one. The `checks` job passes, so typecheck/lint/format/test/build are gated;
  **the browser suite is not gated by CI at all** and only ever runs locally.

- **Two GymOS-Platform defects found and fixed while landing the E2E seed, 2026-08-13.** Both were
  pre-existing and neither was caused by the seed — each was proved so before being touched.

  **The mobile operator journey could not navigate** (`ce4fa4b`). `operator-journey.spec.ts`'s landing
  test clicked the "Gyms" nav link unconditionally, and on a phone that link is `display:none`: the
  persistent sidebar is `hidden lg:flex`, so below `lg` the anchor is in the DOM but outside the
  accessibility tree, and the drawer holding the other copy renders nothing until opened. `getByRole`
  therefore matched **nothing** and waited out the full 30s. Because the block is `describe.serial`,
  that one failure **skipped the nine tests after it** — a whole viewport of the F1 journey reported
  as "did not run" rather than as a failure, which is how it went unnoticed. Measured, not inferred:
  at 390px `getByRole` counts 0 while a raw anchor selector counts 1, and opening the drawer takes it
  to 1. The fix opens the drawer first on mobile, the branch the sibling nav test already used;
  `page.goto('/gyms')` would also have gone green and was rejected, because this file's premise is
  that the mobile pass is the responsiveness gate. **The click came from F3 (`340c1f3c`)**, which
  moved the landing route to `/dashboard` and added a nav hop without the viewport branch — F3's owed
  review is the gate that would have caught it. Suite went 32 passed/1 failed/9 not run → **42 passed,
  0 failed, 0 skipped**.

  **`pnpm format:check` was unpassable on Windows** (`f8050dc`). It failed on 82 files, which looked
  like catastrophic drift and was almost entirely an artefact: Git for Windows defaults to
  `core.autocrlf=true` so a checkout writes CRLF, while Prettier's `endOfLine` defaults to `lf` and
  checks the working tree. A `.gitattributes` with `* text=auto eol=lf` fixes the checkout instead of
  asking every developer to change a global Git setting, and it changed **no committed content** —
  verified, not assumed: no file in the index contained CRLF, so renormalising and re-materialising
  the whole working tree produced a diff of exactly zero content lines. `endOfLine: "auto"` was
  rejected as silencing the gate rather than fixing it. With the artefact gone exactly **one** file
  was genuinely misformatted (an F3 plan doc, cosmetic). Note for the backend's own gate: this is the
  *Platform* repo's equivalent of `81b9ebd` below, and unrelated to it.

- **~~`dotnet format --verify-no-changes` fails~~ — FIXED 2026-08-12 (`81b9ebd`), in the standalone
  pass this note asked for.** Worth keeping for one correction: the entry recorded *seven findings
  across six files* and there were **eight across seven**. `FakeIdempotencyStore.cs` also carried two
  WHITESPACE findings (one object initializer on a single line), pre-existing since the AD-39 commits
  and simply missed when the note was written. The CHARSET findings turned out to be a UTF-8 **BOM the
  `.editorconfig` does not want** — stripped, which is why those files show a one-line diff that
  appears to change nothing. Gate now exits 0.

- **~~⚠️ THE FIX BELOW DOES NOT WORK~~ — genuinely fixed 2026-08-14 (`e235807`), on the third
  attempt.** The catch now names `ConcurrencyConflictException`, which is what
  `UnitOfWork.SaveChangesAsync` actually throws. And the reason this went unnoticed for two days is
  itself now closed: nothing anywhere asserted which exception crosses that boundary, so
  `A_stale_override_delete_surfaces_as_ConcurrencyConflictException_not_the_EF_type` pins it — if
  `UnitOfWork` ever stops translating, that test fails and the catch becomes visibly wrong rather
  than silently so. The log **level** still has no assertion, because that needs a logging package
  which is not on the allowlist; what changed is that the level no longer depends on an unreachable
  branch. The two entries below are kept, struck through, because the sequence — fix, document as
  done, never execute — is the lesson.

- **⚠️ THE FIX BELOW DOES NOT WORK — reopened 2026-08-14 by M4.1's review, and verified by hand.**
  `FeatureOverrideSweepJob` catches `DbUpdateConcurrencyException`, but `UnitOfWork.SaveChangesAsync`
  catches that EF type and rethrows `ConcurrencyConflictException` — its own doc says
  "`DbUpdateConcurrencyException` — an EF type — never reaches the Application layer". So the catch
  `4ce45d0` added is **unreachable dead code**, `LogGymChangedUnderneath` can never fire, and the
  expected operator-vs-sweep race is still reported on the `Error` channel through the generic
  handler. Confirmed at HEAD: the sweep has a `catch (DbUpdateConcurrencyException)` at line 115 and
  `UnitOfWork` throws `ConcurrencyConflictException` at line 33. **The entry below is wrong and is
  kept only so the correction has something to point at.** Note what made this possible: the entry
  itself records that no test asserts the level, so the fix was committed, documented as done, and
  never executed even once on the path it claims to handle.

- **~~`FeatureOverrideSweepJob` logs an expected concurrency race at `Error`~~ — FIXED 2026-08-12
  (`4ce45d0`).** `DbUpdateConcurrencyException` is now caught ahead of the general handler and logged
  at Information as a deferral. Genuine step failures still reach `LogGymFailed` at Error with their
  exception. **The gap it leaves:** no test asserts the level, because the repository has no
  log-capturing test infrastructure at all. Adding one means a testing package that is not on the
  allowlist (`Microsoft.Extensions.Diagnostics.Testing` is the obvious candidate), so it needs
  `docs/Engineering/DEPENDENCY_POLICY.md`'s change process rather than a quiet addition. Until then,
  every `LoggerMessage` level in this repository is unverified by anything but review.

- **The activation review's five findings, all FIXED 2026-08-13 (`a74967f`)**, recorded here because
  three of them are the kind that reappear:
  - **Single-use did not survive concurrency.** The token now carries `xmin`, and the handler turns a
    lost race into the ordinary `link_not_usable` refusal rather than a 409 — the link really is
    spent, so a retry could only fail again. The stale exemption is gone from
    `ConcurrencyModelTests` rather than reworded. Guarded by
    `TwoRequestsThatBothReadOneLiveLink_OnlyOneCanSpendIt`, which uses two `DbContext`s deterministically
    rather than real threads: the window is narrow, and a test that only sometimes hits it is not a gate.
  - **Emailed links dropped the console's sub-path.** The route is relative now, and the base is forced
    to end in a slash — without that, .NET treats the final segment as a file name and drops it anyway,
    so the obvious half-fix is wrong. The integration suite deploys its console under `/admin`
    specifically so the existing link assertion covers this.
  - **`Email:Enabled=false` reported as a successful send.** `EmailSendResult` carries a three-state
    `EmailDispatch`; the header reports `sent`/`failed`/`disabled`; the suppressed send logs at
    **Warning**, because on these two messages "not sent" means a person is waiting for a link that
    will never arrive. `EmailOptions` no longer claims the link can be read out of the log — it never
    could, and that claim is what made the gap invisible.
  - **The check endpoint documented a `410` and returned a `404`.** Description text ships to consumers
    through the contract snapshot, so this was a real contract defect, not a typo.
  - **The read-only check tracked its entity.** `FindByHashForReadingAsync` reads through
    `AsNoTracking`. Kept as a separate method rather than a flag because the command path **must**
    track — `Consume` on a detached entity mutates nothing and leaves the link replayable, which is a
    far worse bug than the one being fixed.

- **F5 on `docker-compose.dcproj` cannot work, and the dcproj's own comment sends you after the
  wrong cause.** Found 2026-08-12. Visual Studio reports *"Cannot find the container for the service
  'api'"*, which that comment attributes to Fast mode's partial build — but `DockerDevelopmentMode`
  is already `Regular`, and the merged config VS writes to `obj/Docker/MergedDockerCompose.cache`
  confirms it (no entrypoint shim, no vsdbg mount). The actual cause is that **VS Container Tools
  does not support `container_name`**: it resolves a service's container by compose's generated
  `<project>-<service>-<index>` name, so `container_name: gymos-api` makes the lookup miss. Every
  service here pins a name, and `.github/workflows/ci.yml` depends on those fixed names in seven
  places (`docker inspect gymos-api`, the read-only and cap-drop assertions), so removing them is not
  free. **Behind that sits a second, independent wall:** even with the container found, `vsdbg`
  cannot attach through `DOTNET_EnableDiagnostics=0`, `read_only: true`, `cap_drop: ALL`,
  `no-new-privileges` and the non-root `USER $APP_UID` — which is the hardening working exactly as
  designed ("dev runs the same hardened image production runs"). **Conclusion: `docker compose up -d`
  is the workflow; F5 on the compose project is not.** For breakpoints, run the host project against
  the containerised dependencies — the dev overlay already publishes Postgres for "Testcontainers-free
  local runs", Seq "for a host-run API to log into", and picks 5080 to match `dotnet run`. Also
  requires the Container Tools component, which VS 2026 does not install by default and whose id
  changed to `Microsoft.VisualStudio.Component.DockerTools` (no dot before `Tools`).

- **The dcproj breaks solution-level `dotnet list package` — and that was failing CI.** FIXED
  2026-08-12 for the CI path. `dotnet list GymOS.Backend.slnx package` reports every real project
  correctly but **exits non-zero** because `docker-compose.dcproj` has no assets file (by design —
  only VS understands `Microsoft.Docker.Sdk`, which is why the solution excludes it from every build
  configuration; the `<Build Project="false" />` exclusion covers building, not this command).
  `build/audit-transitive-packages.ps1` threw on that exit code, so **the "Transitive dependency
  audit (banned packages)" step has been red since the dcproj was added** (`ec47eab`/`a42eafa`) —
  failing on a condition that is not an audit finding, which is the worst kind of red because it
  trains people to ignore it. The script now inspects the reported `problems` instead: the dcproj's
  is expected and ignored, and anything else — an unrestored solution, say — still stops the audit.
  Verified both directions, that it passes now and that a non-dcproj problem is still caught.
  **Still true interactively:** `dotnet list package --outdated` against the solution exits non-zero
  for the same reason, so ad-hoc dependency review has to iterate the `.csproj` files.

- **Three dependency majors are open, and none is a routine bump.**
  `Microsoft.CodeAnalysis.BannedApiAnalyzers` 3.3.4 → 5.6.0 (two majors of an analyzer against
  `TreatWarningsAsErrors`, so new default diagnostics arrive as build errors), `Microsoft.NET.Test.Sdk`
  17.12.0 → 18.8.1, and `xunit.runner.visualstudio` 2.8.2 → 3.1.5. Each wants its own commit and a
  full test run so a break is attributable. Note that **xunit 2.9.3 is the end of the v2 line**: v3
  ships as the separate `xunit.v3` package, which is not on the allowlist, so `ForbiddenPackagesTests`
  would fail the build — it needs `DEPENDENCY_POLICY.md`'s change process, not a version edit.
  Deliberately *not* taken alongside them (`fd7763e`): **PostgreSQL stays on 17** — Neon reports 17.10
  and the sidecar is `postgres:17-alpine`, and parity with the database actually in use beats a newer
  engine — and **Redis stays on 7**, because Redis 8 is AGPL, which this repository's own policy bans.
  Valkey is the BSD-licensed fork if that need ever lands.

- **Five minor findings were deliberately carried out of M4.1's reviews**, each judged not worth the
  change it would cost, and recorded here so the judgement is visible rather than forgotten:
  - **A withdrawal leaves no trace in the application log.** `WithdrawRedundantAsync` returns how many
    exceptions it withdrew and all four synchronous callers discard it; only the sweep logs a count.
    The `gymos.platform.gym_feature.override_cleared` event does reach the outbox, so this is
    legibility rather than auditability — but a support engineer reading logs sees a subscription
    change with nothing saying it deleted an operator's grant.
  - **`IUnitOfWork.DiscardTrackedChanges()` widens a core abstraction for one call site** (the
    retry in `SetGymFeaturesCommandHandler`). The sequence is correct and the doc comment is careful,
    but a future caller outside a bounded retry would silently drop pending state with no compiler or
    test signal. A `BannedSymbols.txt` entry carrying "use only inside a single-attempt retry" would
    close it without another interface.
  - **The plan-edit race test detects a regression roughly one run in eight.** The asymmetry is the
    right way round — it cannot flake red on correct code — and it was proved able to fail by stashing
    the fix. But it is a probe, not a gate; a deterministic interleaving hook would make it one.
  - **"Load the plan by id, map null to `[]`" now exists twice**, in `GymFeatureCommands` and
    `FeatureExceptionMaintainer`, differing only in output shape. Deliberate: the shared helper they
    were extracted from forced one answer onto two different questions ("what does the database say
    now" versus "what will be true after this commit"), which was part of the Critical's root cause.
    Re-sharing it would reintroduce the defect.
  - **`StoredException`'s CA1711 suppression is per-type**, while `.editorconfig` uses
    `allowed_suffixes` for the same analyzer elsewhere. Two mechanisms for one rule; the narrower one
    was chosen deliberately, but the inconsistency is worth knowing.

  Also unfixed at the unit-test layer: no test asserts that `DeleteGymCommandHandler` calls the
  maintainer — that wiring is covered only by its integration test, so a future regression in the
  wiring would not fail fast.


- **Four minor findings were deliberately carried out of M5's reviews**, each judged not worth the
  change it would cost, and recorded here so the judgement is visible rather than forgotten:
  - `ADeletedGymLeavesThePopulationButStillCountsAsChurn` asserts that a gym's creation and
    cancellation share a calendar day. The assertion is what makes the residual UTC-midnight race
    **self-diagnosing** rather than a mystery flake — but it is still a race. Closing it needs an
    `IClock` seam in `AuthenticationHost.Create`, which is shared by 15+ test files.
  - `ActivityEntry.EntityType` coalesces null to `""` while `AuditLogQueries` projects the same
    column as nullable, so `/dashboard/activity` and `/audit-logs` spell "none" two ways. One line
    to reconcile, the day the console says which it wants.
  - Three private copies of the same three-line `AppDbContext` factory exist across the integration
    tests (`AuthenticationHost.CreateDbContext`, `FeatureCatalogSeedingTests`, `PlatformStatsHost`).
    Task 0 collapsed the duplication that actually hurt — seven copies of the HTTP fixtures — and
    these three genuinely differ in what they need.
  - `IDashboardHealthCheck`'s comments claim it is "the one place" an Application abstraction is
    implemented in the host. That is **wrong**, not merely imprecise: `HttpCurrentUser`/`ICurrentUser`
    is the same pattern in the same project. The reasoning the comment gives is sound; only the
    uniqueness claim is false. Fix the sentence next time the file is opened.

- **An expiry recorded late by `RenewalJob` is lost from `expired_gyms` forever** (M5 whole-branch
  review, deliberately deferred): `DailyStatsCompiler` keys `expired_gyms` on `CurrentPeriodEndUtc` —
  the business date a period targets — not on when the expiry was actually noticed, because nothing on
  `GymSubscription` records that moment. `RenewalJob` normally flips a subscription within 15 minutes
  of its period ending, well inside the roughly 3.5–27.5 hour window before `PlatformStatsJob` compiles
  that day at 03:30 UTC. But if `RenewalJob` lags past that window — a subscription that keeps throwing
  inside the sweep, or a deploy window with `Jobs:Enabled=false` — the subscription is still `Active`
  when its day is compiled, and because a stats row is written once and never revisited, that day's
  expiry never gets counted, even after the sweep catches up correctly days later. The fix is an
  `ExpiredAtUtc` column on `GymSubscription`, mirroring `CanceledAtUtc`, so `expired_gyms` keys on when
  the expiry was recorded rather than the date it targets — a schema change to an M3.2 aggregate that
  belongs in its own increment. Reasoning in `docs/BusinessRules/Analytics.md` §11.

- **`canceled_gyms` and `expired_gyms` key on different clocks** (M5 whole-branch review, deliberately
  deferred): `canceled_gyms` counts `CanceledAtUtc`, the real moment cancellation ran; `expired_gyms`
  counts `CurrentPeriodEndUtc`, a business date rather than a moment anything ran. Two subscriptions
  ending within minutes of each other either side of UTC midnight can land on different `stat_date`
  rows, and `GET /analytics/churn` returns both columns plus their `total` as if they were computed the
  same way. Harmless as a known approximation; worth knowing before treating the two columns, or their
  sum, as aligned day-by-day. Reasoning in `docs/BusinessRules/Analytics.md` §11.

- **A gym feature override never expires** (M4 review, still deferred after M4.1): the aggregate
  records who granted the exception, when, and why, but nothing time-bounded. M4.1 built the
  self-cleaning half of the fix — an exception is now withdrawn once it stops *differing* from the
  plan or default, immediately for the gym's own change and within a day via `FeatureOverrideSweepJob`
  otherwise — but nothing withdraws one that simply runs past a time limit, because no override
  carries one. So the goodwill trial that is the headline reason overrides exist still has **no
  ending** — harmless while nothing consumes a resolved flag, and revenue leakage the day enforcement
  lands. The product decision comes first (does an expiring override lapse silently, or does someone
  get told?), then `ExpiresAtUtc` plus one more predicate in the sweep that now already exists, rather
  than a new mechanism. Reasoning in `docs/BusinessRules/Features.md`.

- **`AssignSubscriptionCommandHandler` decides feature redundancy against a plan read with no lock**
  (M4.1 whole-branch review, deliberately deferred): unlike `SetGymFeaturesCommandHandler`, it opens
  no explicit transaction and calls `IPlanRepository.FindByIdAsync`, not `FindByIdForUpdateAsync`,
  before the synchronous `WithdrawRedundantAsync` call decides which of the gym's exceptions the new
  plan makes redundant. A `PUT /plans/{id}/features` that commits between that read and this
  handler's own commit can make the decision stale: T1 assigns a gym to a plan read as `crm = true`,
  T2 concurrently flips that plan's `crm` to `false`, and T1's maintainer — judging against its now-
  stale read — withdraws the gym's `crm = true` override as apparently redundant. The gym ends up on
  a plan that says `crm = false` with the protecting exception gone; withdrawal is a hard delete, so
  the nightly sweep does not restore it. This is a lost **capability**, not corrupted data — the
  stored state is internally consistent and reported truthfully — and it is traceable: the withdrawal
  still raises `GymFeatureOverrideCleared` in the outbox, exactly as an operator-driven clear would.
  **Narrow by construction**: only a plan-feature edit racing a subscription assignment/change on that
  exact plan, inside the few milliseconds between the read and the commit, can lose a capability this
  way; every other interleaving converges to one consistent state or the other. The fix is the same
  shape already proven on `SetGymFeaturesCommandHandler` — an explicit transaction plus
  `FindByIdForUpdateAsync` around the plan read — sized for its own change rather than this wave,
  since it touches the busiest handler in the subscriptions slice. Reasoning in
  `docs/BusinessRules/Features.md`.

- **`GET /features` computes adoption with two correlated subqueries per gym per feature**, with no
  bound and no cache — 12 features × every gym, on every catalogue page load. Fine at present scale
  and deliberately not optimized: the query is written to be readable against `FeatureResolution`,
  and its own comment records that a cleverer earlier version was "correct-looking and
  unverifiable". **M4.1 added `FeatureAdoptionParityTests`**, which pins this exact restatement
  against `FeatureResolution.Resolve` over a seven-gym state matrix — the reason to revisit the shape
  is now a cost problem only, never a correctness one, since a drift (e.g. the deferred expiry work
  adding a fourth `Resolve` input and the SQL copy missing it) fails that test first. Revisit the
  shape itself when the console shows it on a hot path or gym counts reach the thousands.

- **Three Minor findings from M4's review were carried past M4.1, deliberately** (design §6 —
  `docs/superpowers/specs/2026-08-12-m4-review-fixes-design.md`): `PlanQueries.GetAsync`'s four
  correlated subqueries per plan-feature row (bounded at 12 × 4 today; rewriting it earns nothing
  until the catalogue grows); `FeatureRepository.ListAllAsync` being tracked (twelve rows into the
  change tracker for a key lookup — harmless now); and `LogSynced` counting orphans inside "left
  untouched" (true but conflated, cosmetic). None has a live consequence yet; all three are recorded
  in `docs/BusinessRules/Features.md`'s deferred list rather than left only here.

- **Concurrent failed sign-ins for one account collide on the user row's version** (audit finding 7,
  deliberately deferred): four of five parallel guesses raise a concurrency conflict, so
  `/auth/login` can answer **409** on a login endpoint, and `FailedLoginAttempts` advances by one
  instead of five — lockout takes roughly `MaxFailedAttempts` × concurrency to trigger. A user
  double-submitting the form sees the 409 too. The fix is a counter update that does not ride the
  aggregate's optimistic token (a targeted `UPDATE … SET failed_login_attempts = failed_login_attempts + 1`,
  or a dedicated login-attempt row); it is a small domain change that deserves its own increment
  rather than riding along in a ten-finding batch. The per-IP limiter still caps a single source.

- **The dev overlay publishes ports on `0.0.0.0`**, so a running dev stack — with its
  deliberately public admin credentials — is reachable from the local network, not just the
  developer's machine. Harmless against an empty dev database; an exposure the moment a dev
  stack holds data that matters. Fix when convenient: bind to loopback in
  `docker-compose.override.yml` (`"127.0.0.1:5080:8080"`, and likewise postgres/pgadmin/seq/
  minio/mailpit). Decided 2026-08-06 to defer.

- Outbox **processor** (delivery to handlers with SKIP LOCKED claiming) not built yet —
  capture side only; lands with the first event handler need.
- **`platform.audit_logs` is partitioned** (monthly range on `created_at_utc`, migration
  `PartitionAuditLogs`). Future partitions are created by `platform.ensure_audit_log_partitions`,
  called by `AuditLogPartitionMaintainer` — **now on `RetentionJob`'s nightly schedule** (M3.4);
  the startup call stays, because first boot on an empty database needs partitions before the
  first request. Partition **detachment**, the other half of the policy, still waits on the
  counsel-supplied retention window and belongs in the same job when it arrives. A DEFAULT
  partition means an audit write can never fail; rows that land there must be pruned with
  `DELETE` rather than `DETACH`.
- **`IdempotencyRecordMaintainer` (AD-39) purges expired `platform.idempotency_records`** — also
  **folded into `RetentionJob`** (M3.4), keeping its startup call for the same reason. The
  standing instruction to merge the two startup sweeps rather than grow a third is discharged.
- **AD-39 key scope is (tenant, operation, key) — caller-chosen keys are cross-user**: two users
  picking the same key for the same operation collide (spurious 422, or a replay of the other
  caller's response when the hashes match). Per-user scoping would need a superseding ADR;
  mitigated by the API_STANDARDS §10 guidance that keys SHOULD be UUIDs.
- **Permission resolution costs one query per authenticated request** (memoized per request).
  Deliberate: it buys immediate revocation. The cache-with-invalidation version belongs with
  the AD-45 settings/cache work, and should not be added before it is measured.
- `POST /users/{id}/reset-password` (§3.6) is **not** implemented: it sends a time-boxed reset
  link, so it needs email delivery. Invited accounts therefore cannot activate themselves yet —
  the only way to give an account a password today is the `Seed:SuperAdmin` path. This is the
  first thing to build when email lands.
- Role **editing** endpoints do not exist and should not: seeded platform-role grants are
  reconciled to the code at startup. Editable roles are a tenant-scope feature.
- `Asp.Versioning` reports API versions but only v1 exists; the deprecation registry and
  `/api/versions` (AD-37 §6.2) arrive when something is actually deprecated.
- `Argon2PasswordHasher` substitutes a fixed one-byte value for an empty password because
  Konscious throws on zero-length input, which an empty password field would otherwise
  turn into a 500 on an unauthenticated endpoint. Harmless: no storable password is that
  short, so no account can hold the resulting hash.
- EF warning "No IEntityTypeConfiguration found in GymOS.Platform.Infrastructure" is
  gone since M1.1 (configurations now exist).
