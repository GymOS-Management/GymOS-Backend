# Non-Superuser Database Roles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make R14's row-level security actually filter, by connecting the application as a role that PostgreSQL does not exempt from policies.

**Architecture:** Three database roles replace today's single superuser — an admin superuser for humans, a `gymos_migrator` that owns the schema and runs DDL, and a `gymos_app` that owns nothing and serves requests. Migrations move out of the API process into a one-shot compose service using a new `Database:MigrateOnly` host mode. R14's validator then tightens: its connected-role check becomes unconditional, and it additionally asserts the connected role does not own the tables it protects.

**Tech Stack:** .NET 10, EF Core 10, Npgsql, PostgreSQL 17, xUnit, Testcontainers, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-08-15-database-roles-design.md`

## Global Constraints

- No new dependencies. No changes to any `.csproj` or `Directory.Packages.props`.
- `DateTimeOffset.UtcNow` is banned by an analyzer (rule R2/AD-40). Use the injected clock.
- Gate for every task: `dotnet build GymOS.Backend.slnx` at **0 warnings** (warnings are errors) and `dotnet format --verify-no-changes GymOS.Backend.slnx` clean.
- Do NOT push. Commit locally only.
- No change to any row-level security policy. This increment changes only *who connects*.
- Role names, fixed: `gymos_admin` (superuser, `POSTGRES_USER`), `gymos_migrator`, `gymos_app`. Database name stays `gymos`.
- Every test must be watched failing before its fix exists. This project has shipped five guards that could not exhibit the failure they guarded.
- If the build fails with MSB3027/MSB3021, a stray `dotnet run` host holds the build output — stop it rather than working around it.

**Task order is load-bearing.** Task 1 must precede Task 4, because Task 4 makes the role check unconditional and every test host must already be off the superuser. Task 3 must precede Task 4 for the same reason on the deployment side — otherwise there is a commit where `docker compose up` cannot boot.

---

### Task 1: The integration harness stops connecting as a superuser

The riskiest task, deliberately first and alone: it touches one file but changes how 303 passing tests reach the database. If it goes wrong it fails loudly (hosts do not boot) and reverts cleanly.

**Files:**

- Modify: `tests/GymOS.IntegrationTests/PostgresFixture.cs`

**Interfaces:**

- Consumes: nothing from earlier tasks.
- Produces:
  - `PostgresFixture.ConnectionString` — unchanged name, now connects as `gymos_test`
  - `PostgresFixture.CreateDatabaseAsync(string)` / `CreateIsolatedDatabaseAsync(string)` — unchanged signatures, now return `gymos_test` connection strings
  - `PostgresFixture.BootstrapConnectionString` — **new**, the container's superuser, for suites that deliberately need one

**Why the 45 call sites need no edits:** every caller goes through `CreateDatabaseAsync`, `CreateIsolatedDatabaseAsync`, or `ConnectionString`. Changing what those return is the whole change.

- [ ] **Step 1: Read the file you are changing**

Read `tests/GymOS.IntegrationTests/PostgresFixture.cs` in full (81 lines). Note `MaxPoolSizePerHost = 5` and why it exists — the comment explains that ~100 hosts at Npgsql's default pool ceiling exhaust PostgreSQL's `max_connections`, surfacing as `53300: sorry, too many clients already` in unrelated tests. **Do not change that value.**

Also read `tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs`'s `GrantDatabaseToApplicationRoleSql` (around line 53). It already solved this problem for one suite, and its comment records the trap you are about to hit: *"Ownership of `public` moves too, or the role cannot create `__EFMigrationsHistory` and migrations fail on the first statement."*

- [ ] **Step 2: Add the role and the two connection-string forms**

In `PostgresFixture`, add:

```csharp
    /// <summary>
    /// The role every test host connects as. Not a superuser and not <c>BYPASSRLS</c>.
    /// </summary>
    /// <remarks>
    /// Testcontainers' bootstrap role is a superuser, and PostgreSQL exempts a superuser from
    /// row-level security unconditionally — <c>FORCE</c> included. A suite running as one cannot
    /// observe an isolation failure, which is why R14's tenancy suite had to build a restricted role
    /// of its own before any of its assertions meant anything. Doing it here means every suite
    /// inherits that property instead of the next security-sensitive test rediscovering it.
    /// </remarks>
    private const string TestRole = "gymos_test";

    /// <summary>A password for a throwaway role inside a throwaway container; not a secret.</summary>
    private const string TestRolePassword = "gymos-test-role";

    /// <summary>
    /// The container's bootstrap superuser. For suites that deliberately need one — asserting that a
    /// bypassing role is refused, for instance. Everything else uses <see cref="ConnectionString"/>.
    /// </summary>
    public string BootstrapConnectionString => Pooled(_container.GetConnectionString());

    /// <summary>Connection string of the container's default database, as <see cref="TestRole"/>.</summary>
    public string ConnectionString => AsTestRole(_container.GetConnectionString());
```

Change `Pooled` to take an explicit role, and add `AsTestRole`:

```csharp
    private static string Pooled(string baseConnectionString, string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            MaxPoolSize = MaxPoolSizePerHost,
        };

        if (databaseName is not null)
        {
            builder.Database = databaseName;
        }

        return builder.ConnectionString;
    }

    private static string AsTestRole(string baseConnectionString, string? databaseName = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(Pooled(baseConnectionString, databaseName))
        {
            Username = TestRole,
            Password = TestRolePassword,
        };

        return builder.ConnectionString;
    }
```

- [ ] **Step 3: Create the role at container start and hand it the default database**

Replace `InitializeAsync`:

```csharp
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Once per container. `ALTER SCHEMA public` runs here because ExecScriptAsync targets the
        // container's default database, which is the one this statement needs to be inside.
        await _container.ExecScriptAsync(
            $"""
            CREATE ROLE "{TestRole}" LOGIN PASSWORD '{TestRolePassword}' NOSUPERUSER NOBYPASSRLS;
            ALTER SCHEMA public OWNER TO "{TestRole}";
            """);
    }
```

- [ ] **Step 4: Hand each created database to the role**

Replace `CreateDatabaseAsync`:

```csharp
    public async Task<string> CreateDatabaseAsync(string databaseName)
    {
        await _container.ExecScriptAsync($"CREATE DATABASE \"{databaseName}\" OWNER \"{TestRole}\";");

        // Creating the database with an OWNER is not enough on its own: `public` is owned by
        // pg_database_owner, and EF puts __EFMigrationsHistory there. R14's tenancy suite hit exactly
        // this — migrations fail on their first statement without it. It has to run *inside* the new
        // database, which ExecScriptAsync cannot target, so this opens its own connection.
        await using (var connection = new NpgsqlConnection(Pooled(_container.GetConnectionString(), databaseName)))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER SCHEMA public OWNER TO \"{TestRole}\";";
            await command.ExecuteNonQueryAsync();
        }

        return AsTestRole(_container.GetConnectionString(), databaseName);
    }
```

- [ ] **Step 5: Prove the role is not a superuser before trusting 303 tests to it**

Add to `tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs`:

```csharp
    [Fact]
    public async Task TheHarnessRoleIsNotExemptFromRowLevelSecurity()
    {
        // The premise every other test in this suite rests on. If the harness silently handed back a
        // superuser connection, all of them would report "isolation works" while filtering nothing —
        // which is precisely how this suite began life before it built a role of its own.
        await using AppDbContext context = CreateHarnessRoleContext();

        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user";

        await context.Database.OpenConnectionAsync();

        try
        {
            Assert.False((bool)(await command.ExecuteScalarAsync())!);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
```

Write `CreateHarnessRoleContext()` against this suite's existing context helpers, using a connection string obtained from `PostgresFixture.CreateIsolatedDatabaseAsync` rather than the suite's own role.

- [ ] **Step 6: Run it and watch it fail against the old fixture**

Before applying Steps 2-4 (or by temporarily returning `Pooled(...)` from `ConnectionString` again), run:

`dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TheHarnessRoleIsNotExemptFromRowLevelSecurity"`

Expected: **FAIL** — the bootstrap role reports `true`. Record the verbatim output. Then restore Steps 2-4 and watch it pass.

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/GymOS.IntegrationTests`
Expected: 304 passed (303 + 1).

**This is the step that matters.** Any failure here is a host that could not migrate or could not read as the new role. Do not work around it by granting the role more, and do not make it a superuser. Report the failure with its verbatim output — a test that needs superuser rights is telling you something about that test.

- [ ] **Step 8: Gate and commit**

```bash
dotnet build GymOS.Backend.slnx
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "The integration harness stops connecting as a superuser

Testcontainers' bootstrap role is a superuser, and PostgreSQL exempts one from
row-level security entirely, FORCE included. Every host in the suite therefore
ran with the one attribute that makes R14 unobservable — which is why the
tenancy suite had to build a restricted role of its own before any of its six
isolation tests meant anything.

One role per container, owning each test database, handed out by the two
factory methods all 45 call sites already go through. The bootstrap string
stays available under its own name for suites that deliberately need it.

Ownership of public moves with each database: it belongs to pg_database_owner,
EF puts __EFMigrationsHistory there, and migrations fail on their first
statement without it."
```

---

### Task 2: `Database:MigrateOnly`, a host mode that migrates and exits

**Files:**

- Modify: `src/Host/GymOS.WebApi/Program.cs:154-180`
- Test: `tests/GymOS.IntegrationTests/MigrateOnlyTests.cs` (create)

**Interfaces:**

- Consumes: nothing from earlier tasks.
- Produces: configuration key `Database:MigrateOnly` (bool, default false). When true the host applies migrations and terminates with exit code 0; on failure it terminates non-zero without serving.

- [ ] **Step 1: Read the startup block**

Read `src/Host/GymOS.WebApi/Program.cs` lines 154-190. Note the order: optional `MigrateAsync`, then `TenantIsolationValidator`, then `PlatformIdentitySeeder`, `FeatureCatalogSeeder`, `SuperAdminSeeder`, then the app runs. Note also that the whole block is inside a try/catch that logs a startup exception as Fatal and rethrows.

**The seeders and the validator must NOT run in migrate-only mode.** They are the application's job, running as `gymos_app`; the migrator role exists only to apply DDL. Running the validator as the migrator would validate the wrong role.

- [ ] **Step 2: Write the failing test**

Create `tests/GymOS.IntegrationTests/MigrateOnlyTests.cs`:

```csharp
[Fact]
public async Task MigrateOnlyAppliesMigrationsAndDoesNotSeed()
{
    // The migrator's whole job is DDL. If it also seeded, those rows would be written by the role
    // that owns the schema rather than the one that serves requests — and the seeders would run
    // twice in the deployment, once here and once when the API boots.
    string connectionString = await _postgres.CreateIsolatedDatabaseAsync("migrate_only");

    await MigrateOnlyAsync(connectionString);

    await using AppDbContext context = CreateContext(connectionString);

    Assert.Empty(await context.Database.GetPendingMigrationsAsync());

    // `Permission` and `Role` are what PlatformIdentitySeeder writes
    // (PlatformIdentitySeeder.cs:74 and :106). Both empty means the schema exists and nothing seeded
    // it — which is the whole distinction this test draws.
    Assert.False(await context.Set<Permission>().AnyAsync());
    Assert.False(await context.Set<Role>().AnyAsync());
}
```

Write `MigrateOnlyAsync` to boot the host with `["Database:MigrateOnly"] = "true"` and the given connection string, following `AuthenticationHost.Create`'s settings-dictionary shape — note that `Create` currently hard-codes `["Database:MigrateOnStartup"] = "true"`, so your override must turn that off or the two paths will both run.

- [ ] **Step 3: Run it and watch it fail**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~MigrateOnlyTests"`
Expected: FAIL — `Database:MigrateOnly` does not exist, so the host boots normally and the seeders run.

- [ ] **Step 4: Implement the mode**

In `Program.cs`, inside the startup scope and **before** the existing `MigrateOnStartup` block:

```csharp
        // The migrator's mode: apply migrations, then stop. Nothing else runs — the seeders write
        // data and the R14 validator checks the role that serves requests, and neither is this
        // role's job. Deployment runs this as `gymos_migrator`, which owns the schema; the API then
        // starts as `gymos_app`, which owns nothing.
        //
        // The exit code is load-bearing. Compose gates `api` on this container finishing
        // successfully, so exiting 0 after a failed migration would start the API against an
        // unmigrated database — where R14's validator hits its pending-migrations guard and switches
        // the whole control off for that boot. The surrounding try/catch already logs Fatal and
        // rethrows, which produces a non-zero exit; this must stay inside it.
        if (app.Configuration.GetValue<bool>("Database:MigrateOnly"))
        {
            await startupScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

            Log.Information("Migrations applied; exiting because Database:MigrateOnly is set.");
            return 0;
        }
```

Confirm `Program.cs`'s `Main` returns `int`. If it returns `void` or `Task`, change it to `Task<int>` and return 0 at the end of the existing success path — check the file's final lines and the catch block, which likely already returns a non-zero code.

- [ ] **Step 5: Run it and watch it pass**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~MigrateOnlyTests"`
Expected: PASS.

- [ ] **Step 6: Prove the failure path exits non-zero**

Add a second test asserting that a `MigrateOnly` run against an unreachable database does not exit 0. Point it at a connection string with a database name that cannot be created, run it, and assert the exception surfaces rather than being swallowed.

Run it, watch it fail if you write the assertion first, then confirm it passes. **Do not skip this.** The spec names this exit code as the load-bearing part of the task; an untested exit code is the guard this project keeps shipping.

- [ ] **Step 7: Gate and commit**

```bash
dotnet build GymOS.Backend.slnx
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "A host mode that applies migrations and exits

Deployment needs migrations applied by a role that owns the schema, and the
API to run as a role that owns nothing. Rather than putting dotnet ef or a
migration bundle in the runtime image, the host gains a mode: migrate, exit,
never serve. The compose migrator service is then the same image with one flag.

Nothing else runs in this mode. The seeders write data and the R14 validator
checks the role that serves requests; neither is the migrator's job, and
validating the migrator would validate the wrong role.

The exit code is the load-bearing part: compose gates the API on this
container succeeding, so exiting 0 after a failed migration would start the
API against an unmigrated database — where R14's pending-migrations guard
switches the whole control off for that boot."
```

---

### Task 3: The roles exist, and the deployment uses them

**Files:**

- Create: `docker/postgres/initdb/10-roles.sql`
- Modify: `docker-compose.yml` (postgres environment, healthcheck)
- Modify: `docker-compose.override.yml` (api environment, new migrator service)
- Modify: `docker-compose.prod.yml` (secret guards, api environment, migrator service)
- Modify: `src/Host/GymOS.WebApi/appsettings.Development.json`

**Interfaces:**

- Consumes: `Database:MigrateOnly` (Task 2).
- Produces: roles `gymos_admin`, `gymos_migrator`, `gymos_app`; environment variables `GYMOS_MIGRATOR_PASSWORD`, `GYMOS_APP_PASSWORD`; a `migrator` compose service.

- [ ] **Step 1: Read the conventions you must follow**

Read `docker/postgres/initdb/README.md` — it explains that these scripts run **once**, only against an empty data directory, as the superuser, and that the naming convention is a two-digit prefix (`10-roles.sql` is its own example).

Read `docker-compose.yml:58-90`, and in particular the comment above `POSTGRES_PASSWORD`. It records a real trap: **compose interpolates each file independently before merging**, so a `${VAR:?}` marker in `docker-compose.yml` aborts the whole command even when an overlay supplies the value. The established pattern is an empty default in `docker-compose.yml`, a known-public default in `docker-compose.override.yml`, and `${VAR:?}` only in `docker-compose.prod.yml`. **Follow it exactly for both new passwords.**

- [ ] **Step 2: Write the role script**

Create `docker/postgres/initdb/10-roles.sql`:

```sql
-- The application does not connect as a superuser, because PostgreSQL exempts one from row-level
-- security entirely — FORCE included. R14 installs policies on every tenant-scoped table; run as a
-- superuser, not one of them filters a row.
--
-- Two roles, because owning a table and reading it are different privileges. An owner can execute
-- `ALTER TABLE ... NO FORCE ROW LEVEL SECURITY` and switch its own isolation off in one statement,
-- which is well within reach of the SQL injection row-level security mostly exists to contain.
-- gymos_app owns nothing and holds no DDL rights, so that statement is refused outright.
--
-- Runs once, against an empty data directory, as POSTGRES_USER (gymos_admin). See README.md.

CREATE ROLE gymos_migrator LOGIN PASSWORD :'migrator_password' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE gymos_app      LOGIN PASSWORD :'app_password'      NOSUPERUSER NOBYPASSRLS;

-- The migrator owns the database and everything migrations create in it.
ALTER DATABASE :"database_name" OWNER TO gymos_migrator;
ALTER SCHEMA public OWNER TO gymos_migrator;

-- Deliberately without IN SCHEMA. The `platform` schema is created by a migration, so an
-- `IN SCHEMA platform` form would have to run after migrations — by which point it would not cover
-- the tables those migrations had just created. Declared globally and before anything runs, every
-- future table and schema is covered, and a new module cannot ship a table the app cannot read.
--
-- The constraint this creates: these attach only to objects created BY gymos_migrator. Anything that
-- ever creates a table as another role produces a table gymos_app cannot touch.
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gymos_app;
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT USAGE ON SCHEMAS TO gymos_app;

-- No sequence grants: GymOSModelConventions.ApplyClientAssignedKeys makes every key client-assigned,
-- so the database generates nothing.
```

`psql` variables (`:'migrator_password'`) are not set automatically from the environment. The entrypoint runs `.sql` files through `psql` without `-v` bindings, so **either** convert this to a `.sh` script that passes `-v` explicitly, **or** use `\getenv`. Determine which works against `postgres:17-alpine` by running it, and record what you chose and why. Do not leave a password interpolated by shell string-substitution into SQL without confirming the quoting is safe.

- [ ] **Step 3: Update `docker-compose.yml`**

Change `POSTGRES_USER`'s default from `gymos` to `gymos_admin`, add the two password variables following the empty-default pattern, and update the healthcheck's `-U` to match:

```yaml
      POSTGRES_USER: ${POSTGRES_USER:-gymos_admin}
      GYMOS_MIGRATOR_PASSWORD: ${GYMOS_MIGRATOR_PASSWORD:-}
      GYMOS_APP_PASSWORD: ${GYMOS_APP_PASSWORD:-}
```

```yaml
      test: ['CMD-SHELL', 'pg_isready -U ${POSTGRES_USER:-gymos_admin} -d ${POSTGRES_DB:-gymos} -q']
```

- [ ] **Step 4: Add the migrator service and repoint the API**

In `docker-compose.override.yml`, change the API's connection string to `gymos_app`, set `Database__MigrateOnStartup: 'false'`, and add the migrator service:

There is **no YAML anchor** on the `api` service (`docker-compose.yml:115-129`), so the migrator repeats its `build` and `image` rather than inheriting them. Because the same image serves both, `API_IMAGE` must stay identical or the migrator would run a different build from the API.

Add to **`docker-compose.yml`** (the base file, beside `api`), so the service exists in both dev and production and only its environment differs:

```yaml
  # Applies migrations, then exits. `api` waits for it to succeed.
  #
  # Same image as the API, running with Database__MigrateOnly — rather than putting `dotnet ef` or a
  # migration bundle in the runtime image. It connects as gymos_migrator, which owns the schema; the
  # API connects as gymos_app, which owns nothing and therefore cannot alter a table's isolation.
  migrator:
    build:
      context: .
      dockerfile: src/Host/GymOS.WebApi/Dockerfile
    image: ${API_IMAGE:-gymos-api:local}
    container_name: gymos-migrator
    # Not `unless-stopped`: this container is meant to exit, and restarting it would loop forever.
    restart: 'no'
    logging: *default-logging

    environment:
      ASPNETCORE_ENVIRONMENT: ${ASPNETCORE_ENVIRONMENT:-Production}
      Database__MigrateOnly: 'true'
      ConnectionStrings__Database: >-
        Host=postgres;Port=5432;Database=${POSTGRES_DB:-gymos};Username=gymos_migrator;Password=${GYMOS_MIGRATOR_PASSWORD:-}

    depends_on:
      postgres:
        condition: service_healthy
```

Copy the `networks:` block from the `api` service verbatim if it has one — read `docker-compose.yml:115-160` and match it, because a migrator on a different network cannot reach `postgres`.

Then on the `api` service in `docker-compose.yml`, add to its existing `depends_on`:

```yaml
      migrator:
        condition: service_completed_successfully
```

In **`docker-compose.override.yml`**, give the migrator only its development password default, matching how `api` supplies one:

```yaml
  migrator:
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ConnectionStrings__Database: >-
        Host=postgres;Port=5432;Database=${POSTGRES_DB:-gymos};Username=gymos_migrator;Password=${GYMOS_MIGRATOR_PASSWORD:-gymos_dev_migrator_password}
```

and change the `api` service's connection string to `gymos_app`, keeping the `GYMOS_DB_CONNECTION` escape hatch:

```yaml
      ConnectionStrings__Database: >-
        ${GYMOS_DB_CONNECTION:-Host=postgres;Port=5432;Database=${POSTGRES_DB:-gymos};Username=gymos_app;Password=${GYMOS_APP_PASSWORD:-gymos_dev_app_password};Include
        Error Detail=true}
```

and `Database__MigrateOnStartup: 'false'`. Its existing comment says migrations are on in development so that "one command" stays true — rewrite it, since that is now the migrator service's job and the property still holds.

Keep the `GYMOS_DB_CONNECTION` escape hatch working, with `gymos_app` in its default.

- [ ] **Step 5: Mirror it in `docker-compose.prod.yml`**

Add `${GYMOS_MIGRATOR_PASSWORD:?...}` and `${GYMOS_APP_PASSWORD:?...}` guards matching the existing `POSTGRES_PASSWORD` guard's style and message, and give the migrator service the same treatment as the API.

- [ ] **Step 6: Update `appsettings.Development.json`**

```json
    "Database": "Host=localhost;Port=5432;Database=gymos;Username=gymos_app;Password=gymos_dev_app_password"
```

and `"MigrateOnStartup": false`.

- [ ] **Step 7: Verify the stack from empty**

```bash
docker compose down -v
docker compose up -d
docker compose ps
```

Expected: `migrator` exits 0, `api` becomes healthy afterwards. Then confirm the API really is running as the right role:

```bash
docker compose exec postgres psql -U gymos_admin -d gymos -c "SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname LIKE 'gymos%';"
docker compose exec postgres psql -U gymos_admin -d gymos -c "\dt platform.*"
```

Expected: `gymos_migrator` and `gymos_app` both `f`/`f`; every table in `platform` owned by `gymos_migrator`.

Record the actual output. If `migrator` does not exit 0, do not proceed — that is the failure mode Task 2's exit code exists for.

- [ ] **Step 8: Validate both overlays still parse**

```bash
docker compose config --quiet
POSTGRES_PASSWORD=x GYMOS_JWT_SIGNING_KEY=yyyyyyyyyyyyyyyyyyyyyyyyyyyyyyyy GYMOS_MIGRATOR_PASSWORD=x GYMOS_APP_PASSWORD=x API_IMAGE=gymos-api:ci \
  docker compose -f docker-compose.yml -f docker-compose.prod.yml config --quiet
```

Then confirm the production overlay still refuses to run with **no** secrets set, which is what CI asserts:

```bash
docker compose -f docker-compose.yml -f docker-compose.prod.yml config >/dev/null 2>&1 && echo 'BROKEN: rendered without secrets' || echo 'OK: refused'
```

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "Three database roles, and a migrator service to use them

POSTGRES_USER becomes gymos_admin and stops being what the application
connects as. gymos_migrator owns the database and everything migrations
create; gymos_app owns nothing and serves requests. Neither is a superuser or
BYPASSRLS, which is the point: PostgreSQL exempts both from row-level security
outright, so R14's policies filtered nothing in this deployment.

Migrations move into a one-shot service running the same image with
Database:MigrateOnly, and the API waits for it to succeed. MigrateOnStartup is
now false in development too, so dev and production run the same arrangement
rather than two different ones.

Privileges come from ALTER DEFAULT PRIVILEGES with no IN SCHEMA clause: the
platform schema is created by a migration, so a schema-qualified form would
have to run afterwards and would not cover the tables it had just made."
```

---

### Task 4: The validator asserts the arrangement rather than assuming it

**Files:**

- Modify: `src/Platform/GymOS.Platform.Infrastructure/Tenancy/TenantIsolationValidator.cs`
- Modify: `tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs`

**Interfaces:**

- Consumes: `PostgresFixture.BootstrapConnectionString` (Task 1); the deployment roles (Task 3).
- Produces: no new public surface — `ValidateAsync` gains one unconditional check and one new class of problem.

**Why now and not earlier:** the connected-role check can only become unconditional once every test host (Task 1) and the deployment itself (Task 3) are off the superuser. Doing this first would fail every boot.

- [ ] **Step 1: Write the two failing tests**

Add to `TenantIsolationTests.cs`:

```csharp
[Fact]
public async Task ASuperuserFailsTheBootEvenWithNothingTenantScoped()
{
    // The check used to be conditional on something implementing ITenantScoped, purely because the
    // deployment connected as a superuser and an unconditional check would have failed every boot.
    // That is fixed, so the condition goes — which also closes the hole where an entity that forgets
    // the marker silently switches the whole control off.
    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
        () => ValidatorWithNoTenantScopedEntitiesAsSuperuserAsync().ValidateAsync(CancellationToken.None));

    Assert.Contains("BYPASSRLS", error.Message, StringComparison.Ordinal);
}

[Fact]
public async Task ATenantScopedTableOwnedByTheConnectedRoleFailsTheBoot()
{
    // The migrator/app split is the reason an injected statement cannot disable isolation. If the
    // connecting role owns the table, it can run ALTER TABLE ... NO FORCE and the split has silently
    // stopped existing — so the boot asserts the separation rather than trusting the deployment.
    await CreateProbeAsync(TenantProbeSchema.CreateSql);

    InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
        () => ValidatorOwningTheProbeAsync().ValidateAsync(CancellationToken.None));

    Assert.Contains("owns", error.Message, StringComparison.OrdinalIgnoreCase);
}
```

`ValidatorWithNoTenantScopedEntitiesAsSuperuserAsync` already exists from R14's task 5b — reuse it. `ValidatorOwningTheProbeAsync` builds a validator whose connection is the role that created the probe table.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantIsolationTests"`
Expected: both FAIL — the first because the check returns early with nothing tenant-scoped, the second because ownership is not inspected. Record both verbatim.

- [ ] **Step 3: Make the role check unconditional**

In `ValidateAsync`, move `InspectConnectedRoleAsync` **above** the zero-entities early return, and collect its problems regardless:

```csharp
        var problems = new List<string>(await InspectConnectedRoleAsync(cancellationToken));

        var tenantScopedTables = TenantScopedTables().ToList();

        if (tenantScopedTables.Count == 0)
        {
            LogNoTenantScopedEntities(_logger);
        }

        foreach (var (schema, table) in tenantScopedTables)
        {
            problems.AddRange(await InspectAsync(schema, table, cancellationToken));
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "R14 (tenant isolation) is not satisfied, so the host will not start:" +
                Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
```

Note the zero-entities case no longer returns — it logs and falls through, so the role check still runs. Update the XML remarks: the paragraph explaining why the check is conditional is now wrong, and must be replaced with why it is not.

- [ ] **Step 4: Add the ownership check**

Extend `InspectAsync`'s query with an ownership column and assert on it:

```sql
    pg_get_userbyid(c.relowner) = current_user AS owned_by_connected_role,
```

and, alongside the existing four checks:

```csharp
            if (reader.GetBoolean(4))
            {
                problems.Add(
                    $"  {schema}.{table}: the connected role owns this table, so it can execute " +
                    "ALTER TABLE ... NO FORCE ROW LEVEL SECURITY and switch the table's own " +
                    "isolation off. Migrations must run as a role that is not the one serving " +
                    "requests.");
            }
```

Mind the column ordinals — adding a column to the `SELECT` list shifts every `GetBoolean`/`GetInt64` index after it. Put the new column last and confirm each existing index still points where its comment says.

- [ ] **Step 5: Move the tenancy suite to the two-role shape**

The suite currently creates one role that owns its database, which now fails the ownership check. Split it: a migrator role that creates the probe table, and an app role that the validator and the isolation tests connect as, with the default privileges from Task 3's script.

**Every existing test in this suite must still pass unchanged.** If one needs its assertion altered to accommodate the split, that is a finding about the split — report it rather than adjusting the test.

- [ ] **Step 6: Run them and watch them pass**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~TenantIsolationTests"`
Expected: PASS.

- [ ] **Step 7: The test that justifies the whole increment**

Add the pair the spec names as tests 1 and 2 — neither is meaningful alone:

```csharp
[Fact]
public async Task TheAppRoleCannotDisableItsOwnIsolation()
{
    // The reason for two roles rather than one non-superuser role. An owner can turn FORCE off in a
    // single statement, and that statement is within reach of the SQL injection row-level security
    // mostly exists to contain. The app role owns nothing, so PostgreSQL refuses it.
    await CreateProbeAsync(TenantProbeSchema.CreateSql);

    PostgresException error = await Assert.ThrowsAsync<PostgresException>(
        () => ExecuteAsAppRoleAsync("ALTER TABLE platform.tenant_probe NO FORCE ROW LEVEL SECURITY;"));

    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
}

[Fact]
public async Task TheMigratorRoleCanDisableIt_WhichIsWhyTheAppRoleIsNotTheMigrator()
{
    // The control for the test above: it must fail for a privilege reason, not because the statement
    // is malformed, the table is missing, or the connection is broken.
    await CreateProbeAsync(TenantProbeSchema.CreateSql);

    await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe NO FORCE ROW LEVEL SECURITY;");

    await ExecuteAsMigratorRoleAsync("ALTER TABLE platform.tenant_probe FORCE ROW LEVEL SECURITY;");
}
```

Run both. The second passing is what makes the first's `InsufficientPrivilege` meaningful.

- [ ] **Step 8: Prove default privileges reach new tables**

```csharp
[Fact]
public async Task ATableCreatedByTheMigratorIsUsableByTheAppRoleWithNoExplicitGrant()
{
    // The piece most likely to be subtly wrong, and its production failure mode is a permission error
    // on a table a future migration has only just added.
    await ExecuteAsMigratorRoleAsync(
        "CREATE TABLE platform.default_privilege_probe (id uuid PRIMARY KEY);");

    await ExecuteAsAppRoleAsync(
        $"INSERT INTO platform.default_privilege_probe (id) VALUES ('{Guid.NewGuid()}');");

    await ExecuteAsAppRoleAsync("SELECT count(*) FROM platform.default_privilege_probe;");
}
```

This requires the suite's setup to have run the same `ALTER DEFAULT PRIVILEGES` statements as Task 3's script. If it passes without them, the test is not testing what it claims — check by removing them and watching it fail.

- [ ] **Step 9: Full suite and gate**

Run: `dotnet test tests/GymOS.IntegrationTests`

Expected: **311 passed.** The arithmetic, so a mismatch is visible rather than absorbed: 303 today, +1 from Task 1 (the harness-role assertion), +2 from Task 2 (the migrate-only pair), +5 here (two in Step 1, two in Step 7, one in Step 8). Report the real number if it differs; do not adjust the expectation to match.

```bash
dotnet build GymOS.Backend.slnx
dotnet format --verify-no-changes GymOS.Backend.slnx
git add -A
git commit -m "Assert the role arrangement at boot instead of trusting it

The connected-role check stops being conditional. It was gated on something
implementing ITenantScoped only because the deployment connected as a
superuser and an unconditional check would have failed every boot; that is
fixed, and the gate was also the hole through which an entity that forgot the
marker switched the whole control off.

And the validator now refuses to start when the connected role owns a
tenant-scoped table. An owner can execute ALTER TABLE ... NO FORCE ROW LEVEL
SECURITY, which the app role is proved unable to do here — with the migrator
succeeding at the same statement, because a privilege test that cannot be
shown succeeding for the privileged role proves nothing."
```

---

### Task 5: Close out

**Files:**

- Modify: `docs/BusinessRules/TenantIsolation.md`
- Modify: `docs/STATUS.md`
- Create: `docs/runbooks/managed-postgres-roles.md`

- [ ] **Step 1: Business rules**

Add the roles table from the spec's §3, D1's reasoning (why two roles rather than one non-superuser role), and **D2's constraint stated plainly**: default privileges attach only to objects created by `gymos_migrator`, so anything creating a table as another role produces a table the application cannot read. Remove the open item about the superuser, which is now closed, and the `TenantScope` note stays as it is.

- [ ] **Step 2: The Azure runbook**

Create `docs/runbooks/managed-postgres-roles.md`: the same three roles on a managed instance that has no `/docker-entrypoint-initdb.d`, as ordered SQL to run once as the administrator.

**It must open by having the operator check the administrator's own attributes** — `SELECT rolname, rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user` — because Azure's administrator is not a superuser but its `BYPASSRLS` status is unverified, and the runbook must not assume it. Note that R14's validator will refuse the boot if the roles are wrong, and that this is the backstop rather than the plan.

- [ ] **Step 3: STATUS.md**

Read it first and match its structure and voice. Close the superuser open item, record the new roles and the migrator service, update the fresh-machine setup with the `docker compose down -v` requirement and the `dotnet ef database update --connection` command for the bare `dotnet run` workflow, and note that a stale `.env` with the old `POSTGRES_USER` produces a confusing first-boot failure.

- [ ] **Step 4: Every gate, with exact numbers**

```bash
dotnet build GymOS.Backend.slnx
dotnet format --verify-no-changes GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests
dotnet test GymOS.Backend.slnx
docker compose down -v && docker compose up -d && docker compose ps
```

Record what you actually get. Do not round or estimate.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "R14's isolation now actually filters: close-out"
```

**Do not push.** The maintainer authorises pushes to `development`.

---

## Notes for the executor

- **Task order is not negotiable.** Task 4 makes the role check unconditional; Tasks 1 and 3 are what stop that from failing every test host and every `docker compose up` respectively.
- **If a test needs superuser rights to pass, that is a finding, not an obstacle.** Report it rather than widening a role.
- **`docker compose down -v` destroys the database.** That is intended here and the maintainer has approved it, but say so before running it if you are ever unsure.
- **If a task reveals the spec is wrong**, stop and say so rather than implementing around it.
