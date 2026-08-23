# PostgreSQL → SQL Server migration — status

**Branch:** `feature/sqlserver-migration` (cut from `development` @ `f1d3b01`)
**State:** in progress, nothing committed yet — the whole change is in the working tree.
**Last verified good:** both migrations apply to a clean SQL Server 2022 and the two-principal
runtime model works end to end (see “Verified against a real server” below).

---

## 1. Decisions taken (confirmed with the requester)

| Question | Decision |
| --- | --- |
| R14 tenant isolation | **Full equivalent.** SQL Server RLS security policies (FILTER + BLOCK) over `SESSION_CONTEXT('app.tenant_id')`, two principals, validator rewritten against `sys.security_policies`. |
| `audit_logs` partitioning | **Native SQL Server partitioning.** Partition function + scheme, `ensure_audit_log_partitions` as a T-SQL procedure using `SPLIT RANGE`. |
| Concurrency token | **`ulong` over `rowversion`** via `NumberToBytesConverter<ulong>`, so the API's opaque token stays a numeric string. |

---

## 2. Done

### Packages (`Directory.Packages.props`, 3 csproj, `ForbiddenPackagesTests`)
Removed `Npgsql`, `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.OpenTelemetry`,
`Testcontainers.PostgreSql`. Added `Microsoft.EntityFrameworkCore.SqlServer` 10.0.11,
`Microsoft.Data.SqlClient` 6.1.6, `OpenTelemetry.Instrumentation.SqlClient` 1.15.0,
`Testcontainers.MsSql` 4.13.0. `dotnet restore` is clean.

### Provider + configuration
`UseNpgsql` → `UseSqlServer` in the DI extension, the design-time factory, and every test.
Migrations history table moved to the `platform` schema (constants on
`PlatformInfrastructureServiceCollectionExtensions`) — the design-time factory, the runtime host and
the test harness all have to agree on this or migrations look pending to one and applied to another.
`EnableRetryOnFailure` deliberately **not** enabled — it breaks `IUnitOfWork.BeginTransactionAsync`;
written up as the first Azure SQL follow-up.

### Runtime adapters ported to T-SQL
| Was | Now |
| --- | --- |
| `PostgresAdvisoryLock` (`pg_advisory_xact_lock`) | `SqlServerAdvisoryLock` (`sp_getapplock`, `@LockOwner='Transaction'`) |
| `PostgresJobLock` (`pg_try_advisory_lock`) | `SqlServerJobLock` (`sp_getapplock`/`sp_releaseapplock`, `Session`, `@LockTimeout=0`) |
| `PostgresDatabaseClock` (`now()`) | `SqlServerDatabaseClock` (`SYSUTCDATETIME()`) |
| `set_config('app.tenant_id', …)` | `sp_set_session_context` — provider-agnostic `DbConnection`, writes NULL for platform scope |
| `pg_roles` / `pg_class` / `pg_policy` checks | `sys.security_policies`, `sys.security_predicates`, `IS_ROLEMEMBER`, `HAS_PERMS_BY_NAME` |
| `ensure_audit_log_partitions` (plpgsql) | T-SQL procedure, `OUTPUT` parameter, called via raw ADO |
| `next_invoice_number` (plpgsql function) | T-SQL procedure (SQL Server functions cannot do DDL or `NEXT VALUE FOR`) |
| `SELECT … FOR UPDATE` / `FOR SHARE` | `WITH (UPDLOCK, ROWLOCK)` / `WITH (REPEATABLEREAD, ROWLOCK)` |
| `ON CONFLICT DO NOTHING` | `INSERT … SELECT … WHERE NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))` |
| `EF.Functions.ILike(col, p, escape)` | `EF.Functions.Like(col, p)` + CI database collation; `SearchPatterns` switched to `[ ]` bracket escaping |
| `PostgresException.ConstraintName` | parsed out of `SqlException.Message` (2601/2627) — **see risk #1** |
| `NpgsqlException` catches | `SqlException` (3 maintainers) |

### Types
`jsonb` → `nvarchar(max)` (new `JsonConventions`), `numeric(14,4)` → `decimal(14,4)`,
`numeric(5,4)` → `decimal(5,4)`, `xmin`/`uint` → `row_version`/`ulong` (new `ConcurrencyConventions`,
`HasGymOSRowVersion()`), `AreNullsDistinct(false)` dropped (SQL Server's default already treats NULLs
as equal — verified).

### Migrations — recreated, not converted
All 17 Npgsql migrations + designers + snapshot deleted. Two new ones:
- `20260823193125_InitialSqlServer` — scaffolded from the model.
- `20260823193131_RuntimeDatabaseProgrammability` — hand-written: RLS predicate function,
  `next_invoice_number`, partition function/scheme + clustered-PK rebuild + 4 aligned indexes,
  `ensure_audit_log_partitions`. Both runtime procedures are `WITH EXECUTE AS OWNER` (the
  `SECURITY DEFINER` equivalent).

**Schema fidelity check passed.** Diffing the old and new model snapshots, the *only* differences
across every table, column, key, index, uniqueness, filter, max-length and delete-behaviour are the
two intended ones: 10 × `xmin` → `row_version`, and +10 `IsRequired()` (rowversion is non-nullable).

### Docker
`postgres` → `mssql` (2022, Developer, `Latin1_General_100_CI_AS`, `mssql-data` volume, sqlcmd health
check, no `cap_add` needed). New one-shot **`db-init`** service — the SQL Server image has no
`/docker-entrypoint-initdb.d`, so initialisation is an explicit, idempotent step in the dependency
graph. `docker/postgres/` deleted, `docker/sqlserver/initdb/` added (`00-database.sql`,
`10-principals.sql`, `README.md`). `migrator` gates on `db-init`; `api` gates on `migrator`.
Both overlays updated; **`docker compose config` passes for both**, and the production overlay still
refuses to render without secrets. pgAdmin removed (cannot speak to SQL Server) — replaced by
documented `sqlcmd`/SSMS/ADS access, not another container.

### Test harness (partly done)
`PostgresFixture` → `SqlServerFixture` (Testcontainers.MsSql, per-suite databases, two principals,
schema pre-creation, standing `GRANT EXECUTE ON SCHEMA::platform`). New `TestDatabase.Options`
helper so all 26 context call sites configure the history table identically. Mechanical renames
applied suite-wide.

---

## 3. Verified against a real server

Container `gymos-mssql-dev` (port **11433**, `sa` / `Gymos_dev_sa_password1`) is still running with a
migrated `gymos` database. Confirmed there:

- Both migrations apply cleanly to an empty database as `gymos_migrator`.
- 24 tables, 75 indexes, 22 FKs, 10 `rowversion` columns, 2 procedures, 2 functions,
  1 partition function with 4 boundaries (5 partitions), collation `Latin1_General_100_CI_AS`.
- Filtered indexes carry the right predicates, including `[status] IN ((1),(2),(3))`.
- The `roles` and `user_roles` unique indexes are **not** filtered — see risk #2.
- Schemas owned by `gymos_migrator`, so `EXECUTE AS OWNER` resolves correctly.
- As `gymos_app` (no DDL, no security admin): `next_invoice_number` returns 1, 2;
  `ensure_audit_log_partitions` created 3 boundaries then 0 on re-run; `sp_set_session_context` and
  `sp_getapplock`/`sp_releaseapplock` work; `CREATE TABLE` is correctly refused.
- RLS proved separately: FILTER + BLOCK predicates over `SESSION_CONTEXT` filter **`sa` itself**
  (tenant A sees 1 row, platform scope sees 0, cross-tenant insert refused). This is why
  `FORCE ROW LEVEL SECURITY` has no counterpart and needs none.
- `datetimeoffset` is valid as a partitioning column; a far-future row inserts with no boundary for
  its month, so no DEFAULT partition is required.

---

## 4. Remaining work — pick up here

### 4.1 Finish the build (immediate next step)
`dotnet build GymOS.Backend.slnx` was down to a handful of errors. The last sed pass fixed the
`uint`→`ulong` fakes; **re-run the build first** and work the list. Known outstanding:

- `tests/GymOS.IntegrationTests/MigrateOnlyTests.cs` — still references `PostgresException`,
  `PostgresErrorCodes.InsufficientPrivilege`, and a `ThrowsPostgresExceptionAsync` helper.
  Needs `SqlException` + error number **229/262** (permission denied) instead of SQLSTATE `42501`.
- `tests/GymOS.IntegrationTests/InvoicePersistenceTests.cs:173-185` — expects `PostgresException`
  with `PostgresErrorCodes.CheckViolation`; SQL Server equivalent is `SqlException` number **547**.
- `tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs:482` — `PostgresException` again,
  plus `NpgsqlDataReader` at line 608.
- `ConcurrencyModelTests.cs:135` asserts `token.GetColumnName() == "xmin"` → `"row_version"`;
  line 183 `InlineData("4294967296") // one past uint.MaxValue` is now a *valid* ulong and must
  become `18446744073709551616`.

### 4.2 Test files needing real review (not mechanical)
These encode PostgreSQL behaviour in their assertions:

| File | What has to change |
| --- | --- |
| `Tenancy/TenantProbeSchema.cs` | `ENABLE/FORCE ROW LEVEL SECURITY` + `CREATE POLICY` → `CREATE SECURITY POLICY` with `platform.fn_tenant_isolation` (the migration now owns the predicate). |
| `Tenancy/TenantIsolationTests.cs` (874 lines) | The largest job. `NO FORCE` tests have no counterpart — replace with "the app principal cannot `ALTER SECURITY POLICY … STATE = OFF`". The `pg_proc`/`prosecdef` "definer functions" query needs `sys.sql_modules` + `execute_as_principal_id`. |
| `AuditLogPartitioningTests.cs` | Partition **tables** → partition **boundaries**; no DEFAULT partition exists. |
| `MigrateOnlyTests.cs` | "permission denied to create database" — SQL Server wording and number differ. |
| `SubscriptionApiTests.cs:185` | Comment asserts Postgres normalises `status IN (1,2,3)`; SQL Server renders `[status] IN ((1),(2),(3))`. |
| `JobLockTests.cs` | Should pass unchanged, but the "connection dies → lock released" test depends on session-lock semantics — re-verify. |

### 4.3 Not started
- **CI** — `GymOS-Backend/.github/workflows/ci.yml` (health-wait on `gymos-postgres`, the
  `postgres networks=gymos-data` assertion, prod-overlay secret env vars) and
  `GymOS-Platform/.github/workflows/ci.yml`.
- **E2E** — `GymOS-Platform/tests/e2e/global-setup.ts` shells `docker exec … psql`;
  `seed-analytics.sql` is PostgreSQL syntax (`max(stat_date) - min(stat_date) + 1`);
  `playwright.config.ts`; `tests/e2e/README.md`.
- **Docs** — ~25 files. Highest value: `docs/Infrastructure/DOCKER.md`,
  `docs/BusinessRules/TenantIsolation.md`, `docs/Engineering/DEPENDENCY_POLICY.md`, and
  `docs/runbooks/managed-postgres-roles.md` → `managed-sqlserver-roles.md` (**referenced by name from
  code comments already written — this one is load-bearing**).
- **Full stack boot** — `docker compose up -d --build` was started and interrupted; never completed.
- **Validation** — `dotnet build` / `test` / `format --verify-no-changes` have not passed yet.

---

## 5. Open risks to review

1. **Constraint-name parsing in `UnitOfWork`.** `SqlException` has no `ConstraintName`, so the name is
   recovered from the message text (2601 → 2nd quoted token, 2627 → 1st). `SetGymFeaturesCommand`
   retries on exactly one constraint name, so this is load-bearing. Mitigated by pinning
   `Current Language=us_english` in every connection string. Worth a dedicated test.
2. **EF adds `WHERE [tenant_id] IS NOT NULL` to unique indexes over nullable columns, silently.**
   Caught and suppressed with `.HasFilter(null)` on `roles` and `user_roles`; without it, platform-scope
   duplicates would have been allowed. **Any future unique index over a nullable column needs the same.**
3. **Case-insensitive collation** is a real behavioural change from `lc-collate=C`. Unique indexes on
   text are now CI. Every such column is normalised before storage, so it is unreachable through the
   application — but it should be stated in the release notes.
4. **`user_activation_tokens.created_by_user_id`** moved from `SetNull` to `ClientSetNull` — SQL Server
   refuses two cascade paths to `users` (error 1785). No user-deletion path exists today, so it is
   dormant; if one is added it must null the column first.
5. **`sys.database_principals` is permission-filtered.** Cost an hour: a guarded `GRANT` inside a
   migration is silently skipped because `gymos_migrator` cannot *see* `gymos_app`. Grants now live in
   bootstrap as `GRANT EXECUTE ON SCHEMA::platform`. Don't reintroduce the guarded form.

---

## 6. Handy commands

```bash
# scratch server used for verification (still running)
docker exec -i gymos-mssql-dev /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P 'Gymos_dev_sa_password1' -d gymos -C -I -W -Q "SELECT name FROM sys.tables"

# migrate a clean database as the migrator
export GYMOS_DATABASE='Server=localhost,11433;Database=gymos;User Id=gymos_migrator;Password=Gymos_dev_migrator_password1;Encrypt=True;TrustServerCertificate=True;Current Language=us_english'
dotnet ef database update --project src/Platform/GymOS.Platform.Infrastructure \
  --startup-project src/Platform/GymOS.Platform.Infrastructure

# regenerate the initial migration (keep RuntimeDatabaseProgrammability's body — copy it aside first)
dotnet ef migrations remove --project src/Platform/GymOS.Platform.Infrastructure \
  --startup-project src/Platform/GymOS.Platform.Infrastructure --force
```

**Note for Git Bash:** `export MSYS_NO_PATHCONV=1` before any `docker exec … /opt/...` or the path is
mangled to `C:/Program Files/Git/opt/...`. Pipe SQL in via `docker exec -i … < file` rather than
`docker cp`.
