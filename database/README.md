# The GymOS database project

`GymOS.Database` is the declarative record of the schema the EF Core migrations produce. It exists
to be **built, compared and read** — never to deploy.

---

## Which one is responsible for what

| | EF Core migrations | This project |
|---|---|---|
| Changes a database | **Yes — always, everywhere** | **Never** |
| Source of truth for schema *change* | Yes | No |
| Declarative record of the *result* | No | Yes |
| Runs in deployment | Yes (`Database:MigrateOnly`) | No |
| Answers "does UAT match what we think?" | No | Yes |

**Nothing in this directory may ever be published to a database.** That is the whole design. Two
mechanisms that can both mutate a schema will eventually disagree about which one did, and the
disagreement surfaces as a production incident rather than a build failure. EF migrations mutate;
this project observes.

If a comparison shows drift, **the fix is an EF migration**, never the script SqlPackage generated.

---

## Why it exists at all

The migrations are imperative: a sequence of changes, correct only when replayed in order from
empty. That answers "how did we get here" but not "where are we", and it cannot answer "does the
database in front of me actually match?". Nothing did, before this.

Concretely it gives four things:

1. **The schema in Git, readable.** 23 tables as 23 files, rather than as a 1,600-line C# migration.
2. **Validation in CI.** The build parses every object against the Azure SQL dialect, so a migration
   producing something Azure SQL rejects fails the build rather than the deployment.
3. **Comparison against a live database**, read-only.
4. **Drift detection** — someone changing UAT by hand is currently invisible.

---

## How the schema is maintained

**By regenerating it from the migrations. Never by hand.**

```
dotnet ef migrations script --no-build
  --project src/Platform/GymOS.Platform.Infrastructure
  --startup-project src/Host/GymOS.WebApi
  --output artifacts/schema.sql
```

Every file here was derived from that output, so a hand edit is a lie the next regeneration silently
corrects. When you add a migration, regenerate the affected object files in the same commit — this
project is only useful while it tracks the migrations exactly.

Each file carries a header saying so, for whoever opens one without reading this.

### Layout

```
database/GymOS.Database/
  Security/               schemas: platform, outbox
  Tables/platform/        21 tables, each with its own indexes and constraints
  Tables/outbox/          messages, deliveries
  Programmability/
    Functions/            fn_tenant_isolation, audit_log_boundary_exists
    StoredProcedures/     next_invoice_number, ensure_audit_log_partitions
  Storage/                pf_audit_logs_monthly, ps_audit_logs_monthly
```

Indexes live in their table's file rather than separately: a table's shape stays in one place, and
SSDT accepts either. There are no views, triggers or security policies because **GymOS has none** —
`fn_tenant_isolation` is the row-level-security predicate, but no `SECURITY POLICY` binds it yet,
because no entity implements `ITenantScoped`. Empty folders implying otherwise would be a lie.

---

## Three objects that are NOT comparable, and why

Read this before trusting any report.

### 1. `pf_audit_logs_monthly` — its boundaries are runtime state

The migration creates it with a boundary derived from `SYSUTCDATETIME()`, and
`ensure_audit_log_partitions` splits another every month for the life of the database. Its range
list is *state*, not schema: two databases migrated on different days already disagree, and every
database disagrees with this repository the month after a file is written.

The declared boundaries are a snapshot and **are expected to be wrong**. The object is declared only
because the `audit_logs` primary key and four of its indexes are stored `ON ps_audit_logs_monthly`,
and a project missing the function does not build.

Comparison passes `/p:IgnorePartitionSchemes=True` so it is never diffed. **Publishing would
reconcile the live boundary list against this one with `MERGE RANGE`, which moves audit rows between
partitions and can destroy them.**

### 2. `platform.invoice_number_seq_<year>` — created on demand, in no migration

`next_invoice_number` creates one sequence per calendar year, at runtime, the first time an invoice
is issued that year. They appear in no migration and cannot be declared here without being wrong on
1 January.

They will always report as "in target, not in source". That is correct, and must stay harmless:
`DropObjectsNotInSource` is **False** and must remain so. Dropping them restarts invoice numbering
at 1 and breaks a legally consecutive series.

### 3. `__EFMigrationsHistory` and the database principals

EF's own bookkeeping, plus `gymos_app` and `gymos_migrator`, which carry passwords and differ per
environment (`docs/runbooks/managed-sqlserver-roles.md`). None belongs in source control, so
comparison excludes `Users;Logins;RoleMembership;Permissions`.

---

## Commands

### Build, and produce a DACPAC

```
dotnet build database/GymOS.Database/GymOS.Database.sqlproj --configuration Release
```

`dotnet build GymOS.Backend.slnx` builds it too — the project is part of the solution, so CI
validates the schema as a side effect of the ordinary build and uploads the dacpac as an artifact.

**Opening it in Visual Studio needs the "Data storage and processing" workload** (SQL Server Data
Tools), from the Visual Studio Installer. Without it the IDE cannot load the project type, reports
the solution as containing an incompatible project — `00d1a9c2-b5f0-4af3-8072-f6c62b433612`, the
SSDT GUID — and writes an `UpgradeLog.htm`, which is gitignored for exactly that reason. The
command line and CI are unaffected either way; this is purely an IDE component.

SSMS is a Visual Studio shell without SSDT and cannot open the project at all. That is expected and
not worth working around: SSMS is for running queries.

Output: `database/GymOS.Database/bin/Release/GymOS.Database.dacpac`. The dacpac *is* the build
output; there is no separate packaging step. It needs no Visual Studio and no Windows —
`Microsoft.Build.Sql` is cross-platform, which is what lets CI produce it.

### Compare against a database — Visual Studio

With the SSDT workload installed: right-click the project → **Schema Compare**, or
**Tools → SQL Server → New Schema Comparison**. Source = this project (or the built `.dacpac`),
target = the database.

Set two options (⚙) before comparing, for the reasons in the section above:

- **Ignore partition schemes** → on
- **Drop objects not in source** → off

**Use `Generate Script`. Never `Update`.** `Update` writes to the target, which is the one thing
this project must never do.

### Compare against a database — command line

```
./scripts/schema-compare.ps1 -ConnectionString "<target connection string>"
```

Read-only. It writes `artifacts/schema-compare/drift-report.xml` and `would-apply.sql`, prints a
summary, and executes nothing. A read-only login is sufficient — prefer one.

### Generate a deployment script without executing it

`would-apply.sql` from the command above already is one. Directly:

```
dotnet sqlpackage /Action:Script
  /SourceFile:database/GymOS.Database/bin/Release/GymOS.Database.dacpac
  /TargetConnectionString:"<target>"
  /OutputPath:artifacts/would-apply.sql
  /p:IgnorePartitionSchemes=True
  /p:DropObjectsNotInSource=False
```

**`/Action:Script` and `/Action:DeployReport` are the only two actions anyone should run against a
GymOS database. `/Action:Publish` is the one that must never be used.**

> Run SqlPackage from PowerShell, not Git Bash. Bash rewrites a leading `/Action:...` into a
> filesystem path, and the command fails in a way that does not explain itself.

### Reviewing drift

1. Run the comparison.
2. `NO DRIFT` — done.
3. Otherwise read `drift-report.xml`. Expect the three exclusions above; anything else is real.
4. Real drift means either someone changed the database outside the migrations, **or** this project
   was not regenerated after a migration landed. Check the second first — it is far more likely.
5. Fix by writing an EF migration and regenerating this project. Never by running the script.

---

## Toolchain

| | |
|---|---|
| `Microsoft.Build.Sql` | **2.2.0**, pinned in the `.sqlproj` |
| `microsoft.sqlpackage` | 170.4.83, in `dotnet-tools.json` — `dotnet tool restore` |

**Version 1.0.0 of `Microsoft.Build.Sql` does not work on .NET SDK 10.** It fails with `MSB4019`,
importing `NuGet.Build.Tasks.Pack.targets` from a path the SDK no longer has. If the build ever
breaks with that error, the version was lowered.

`database/Directory.Build.props` is deliberately empty and deliberately present: it terminates the
MSBuild lookup chain so the repository-root `Directory.Build.props` — `TargetFramework=net10.0`,
`TreatWarningsAsErrors`, C# analyzers, `BannedSymbols.txt` — never reaches a `.sqlproj`, none of
which applies to one and several of which break it.

---

## What is not done yet

**The CI workflow is now written** — `.github/workflows/schema-drift.yml` — and the credential
decision it was waiting on is made: it authenticates with the **existing** deploy identity by
federated OIDC, then mints a Microsoft Entra access token for the SQL resource. There is no
connection string and no password anywhere in it, which was the whole point of waiting rather than
adding a secret.

It stays **read-only** and structurally cannot be otherwise: `/Action:DeployReport` and
`/Action:Script` only, artifacts uploaded for a human to read, never `/Action:Publish`. There is no
flag on the workflow or the script that publishes.

**What remains is two Azure grants only an administrator can make**: an Entra admin on the UAT SQL
server, and `db_datareader` plus `VIEW DEFINITION` for `[gymos-uat-deploy]` on `GymOS_UAT`. The exact
statements are in `docs/Infrastructure/AZURE.md` §3. Until they exist the workflow is gated off by
the repository variable `SCHEMA_DRIFT_ENABLED` and reports why it skipped; run the comparison locally
in the meantime.

Once enabled, it annotates drift without failing the run until `SCHEMA_DRIFT_ENFORCING` is set —
a check that starts out red teaches everyone to ignore it.

One limitation is worth knowing rather than discovering: **an object present in the database and
absent from this project is not reported at all**, because `DropObjectsNotInSource=False` is required
for the three uncomparable objects above. `docs/Architecture/DATABASE.md` §4 records the gap, what
covers it instead, and what closing it would take.
