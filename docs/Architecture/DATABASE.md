# The database: two representations, one authority

GymOS holds its schema twice, on purpose, and confusing the two is the one mistake this document
exists to prevent.

| | EF Core migrations | Database project (`database/GymOS.Database`) |
|---|---|---|
| **Is** | the migration history and the deployment mechanism | the declarative record of the schema those migrations produce |
| **Changes a database** | **yes — the only thing that does, in every environment** | **never** |
| **Used for** | applying schema change | validation, comparison, drift detection, DACPAC |
| **Lives in** | `src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations` | `database/GymOS.Database` |
| **Authored** | by `dotnet ef migrations add` | generated from `dotnet ef migrations script`, never hand-written |
| **Publishes** | n/a | **no.** No workflow, script or documented command in this repository publishes it |

**These are not two migration systems.** There is one: EF Core. The database project is a *mirror*
that can be built, compared and read, and the moment it is treated as a deployment source the two
representations start competing to define the same schema — which is the failure mode this
arrangement is shaped to avoid. `scripts/schema-compare.ps1` only ever invokes `/Action:DeployReport`
and `/Action:Script`; there is no code path in it that publishes, and that is deliberate rather than
incidental.

---

## 1. Why hold it twice at all

An EF migration is an ordered list of *changes*. It answers "how did we get here" and it cannot
answer "what should be here" without replaying every migration in order — which is exactly what you
cannot do against a live database to check it.

A declarative schema answers the second question directly. That buys three things worth the
duplication:

1. **Validation at build time.** The project targets Azure SQL (`SqlAzureV12DatabaseSchemaProvider`),
   so a migration producing something Azure SQL rejects fails in CI rather than in a deployment.
2. **Comparison against a live database.** Drift is otherwise invisible until a deployment behaves
   differently in production than in UAT.
3. **A reviewable schema in Git.** A pull request that changes a table shows the table changing,
   not a C# `MigrationBuilder` call.

---

## 2. What the project contains

Generated from the migrations, laid out by object:

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

Indexes live in their table's file rather than separately, so a table's shape stays in one place.

There are **no views, triggers or security policies in the project because the schema has none**.
`fn_tenant_isolation` is the row-level-security predicate, but no `SECURITY POLICY` binds it yet —
no entity implements `ITenantScoped`. Empty folders implying otherwise would be a lie, and the
moment the first policy exists it is generated into `Security/` like everything else.

Three objects are deliberately **not** comparable, and publishing any of them would be actively
harmful: the audit partition function (its boundaries are runtime state), the per-year invoice
sequences (dropping one restarts a legally consecutive invoice series at 1), and
`__EFMigrationsHistory` with the database principals. The reasoning is in
[`database/README.md`](../../database/README.md), which is the reference for the project itself.

---

## 3. How a schema change is made

```
1. Change the model.
2. dotnet ef migrations add <Name>          ← the change, as EF sees it
3. dotnet ef migrations script <from> <to>  ← the resulting T-SQL
4. Update database/GymOS.Database from that script, object by object
5. dotnet build database/GymOS.Database/GymOS.Database.sqlproj   ← Azure SQL validation
6. Commit both halves together
```

Step 6 is the rule that keeps the two representations honest: **a migration and its declarative
counterpart land in the same commit.** A migration merged without it makes every subsequent drift
report wrong, and a reviewer cannot tell a real schema difference from a bookkeeping omission.

Step 4 is a transcription, not a design activity. Never hand-author the declarative side: if it
disagrees with the migration, the migration is what runs.

---

## 4. Drift detection

```
Git (database project) → build → DACPAC → compare against UAT → report → human → (maybe) a migration
```

Read-only at every step. The comparison never modifies UAT, and the "would apply" script it
generates exists to be *read*, never run — the fix for drift is always a migration.

Locally:

```
./scripts/schema-compare.ps1 -ConnectionString '<a read-only connection string>'
```

In CI: `.github/workflows/schema-drift.yml`, weekday mornings and on commits that touch migrations
or the project. It authenticates by federated OIDC and a Microsoft Entra access token — **no
connection string and no password anywhere in the workflow** — which is why it waited for the Entra
grant rather than shipping with a new secret. Enable it with the repository variable
`SCHEMA_DRIFT_ENABLED`; make it fail the run with `SCHEMA_DRIFT_ENFORCING` once a few runs have
proved it quiet. A check that starts out red teaches everyone to ignore it.

Exit codes are three, not two: **0** clean, **2** drift, **1** could not compare. The third is not a
pass and never reports as one — "no answer" and "no drift" are different facts.

### What it detects

Missing tables, columns, indexes, constraints and programmable objects; changed data types,
nullability, index definitions, constraint definitions, procedure, view and function bodies.

### What it cannot detect, and why

**An object present in UAT and absent from the repository** — an unexpected table added by hand, for
instance. `DropObjectsNotInSource=False` is what makes that invisible, and it is not adjustable: with
it True, the three legitimately-uncomparable objects above are reported as drift on *every* run, and
a check that always fails is a check nobody reads.

The gap is covered elsewhere rather than left open. `TenantIsolationValidator` fails startup on a
tenant-scoped table without a forced policy; the role split means the application principal cannot
create an object in the first place; and unexpected objects have in practice come from a human with
an admin connection, which is what
[`docs/runbooks/managed-sqlserver-roles.md`](../runbooks/managed-sqlserver-roles.md) governs. If that
proves insufficient, the answer is a second comparison run with `DropObjectsNotInSource=True` and an
explicit ignore-list for the three known objects — an addition, not a change to this one.

---

## 5. The DACPAC

Built by `dotnet build` on the project — no Visual Studio, no Windows requirement, which is what
lets CI produce it.

```
# Locally
dotnet build database/GymOS.Database/GymOS.Database.sqlproj --configuration Release
# → database/GymOS.Database/bin/Release/GymOS.Database.dacpac
```

In CI it is built as its own step and uploaded as the artifact **`gymos-database-dacpac`** on every
green run, so any commit can be compared against a live database without rebuilding it.

**A trap that has already cost a green build:** `dotnet build` on `GymOS.Backend.slnx` **silently
skips the `.sqlproj`.** It reports success and produces nothing at all. The project is in the
solution so Visual Studio can show it and run Schema Compare from it, but CI must — and does —
build it by path.

To inspect one: a DACPAC is a zip. `model.xml` inside it is the schema.

```
unzip -p GymOS.Database.dacpac model.xml | head -50
```

To generate a deployment script **without executing it** — the only "deployment" this project is
ever used for, and the output is for reading:

```
dotnet sqlpackage /Action:Script `
  /SourceFile:database/GymOS.Database/bin/Release/GymOS.Database.dacpac `
  /TargetConnectionString:'<target>' `
  /OutputPath:artifacts/would-apply.sql
```

`scripts/schema-compare.ps1` does exactly this, with the comparison options that make the output
meaningful, and never runs the result.

---

## 6. Visual Studio

Opening the solution needs the **"Data storage and processing"** workload (SQL Server Data Tools).
Without it the IDE cannot load the project type and reports the solution as containing an
incompatible project.

Schema Compare defaults are wrong for this project in one specific way: it writes a *second* copy of
every object under its own `BySchemaAndSchemaType` layout and rewrites the `.sqlproj` with explicit
`<Build Include>` items. Run it **source = project, target = database**, and use **Generate Script**
— never **Update**.
