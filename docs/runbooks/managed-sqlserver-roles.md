# Provisioning the GymOS principals on a managed SQL Server instance

**Applies to:** Azure SQL Database (AD-11), Azure SQL Managed Instance, or any SQL Server you
administer through a login rather than the container. **Not applicable to the Docker deployment** —
there, the `db-init` service applies `docker/sqlserver/initdb/*.sql` for you. This runbook exists
because a managed instance has no such service: nobody runs those files for you, and on Azure SQL
Database two of their statements do not work as written.

**Status:** documented, not provisioned. Nothing on Azure exists yet for GymOS. This is recorded
ahead of that day so whoever provisions the first managed instance has the ordered statements rather
than a blank cursor, and so the shape matches `docker/sqlserver/initdb/10-principals.sql` and
`docs/BusinessRules/TenantIsolation.md` rather than being reinvented in isolation.

**Replaces `managed-postgres-roles.md`,** which covered the same ground for Azure Database for
PostgreSQL. Where a step differs because the engine differs — and several do, materially — the
difference is called out rather than quietly rewritten.

---

## What you are creating, and why two principals

| Principal | Holds | Used by |
| --- | --- | --- |
| `gymos_migrator` | `db_ddladmin`, `db_datareader`, `db_datawriter`, `ALTER ANY SECURITY POLICY`; owns the `platform` and `outbox` schemas | The one-shot migration step only |
| `gymos_app` | `db_datareader`, `db_datawriter`, `EXECUTE` on `platform` | Every API instance, every request |

The split is not tidiness. R14's tenant isolation is a set of security policies, and a principal
holding `ALTER ANY SECURITY POLICY` — which `db_owner` and the server admin both do — can switch them
all off in one statement:

```sql
ALTER SECURITY POLICY platform.some_policy WITH (STATE = OFF);
```

That statement is well within reach of the SQL injection row-level security mostly exists to contain.
`gymos_app` cannot issue it. **`TenantIsolationValidator` refuses to start the host if the connected
principal can**, so getting this wrong is a failed deployment rather than a silent loss of isolation.

### One thing that got simpler than the PostgreSQL version

PostgreSQL exempts a superuser and any `BYPASSRLS` role from row-level security **at read time**, and
exempts a table's owner unless `FORCE ROW LEVEL SECURITY` is set. The PostgreSQL runbook therefore
opened by making you check `rolsuper` and `rolbypassrls` on the administrator login, because a
privileged connection would have quietly read across every tenant.

**SQL Server has no read-time exemption of any kind.** A security policy filters every principal that
queries the table — schema owner, `dbo`, `sysadmin`, all of them. So there is no equivalent check to
perform and no `FORCE` to set. The only remaining risk is a principal *disabling* the policy, which is
what the table above is arranged to prevent.

---

## Step 0 — know which product you are on

The next step is the one that differs between Azure SQL Database and everything else. Find out which
you have:

```sql
SELECT SERVERPROPERTY('EngineEdition') AS engine_edition,
       SERVERPROPERTY('ProductVersion') AS version,
       DB_NAME() AS current_database;
```

| `engine_edition` | Product | Use |
| --- | --- | --- |
| 5 | Azure SQL Database | **Step 1a** (contained users) |
| 8 | Azure SQL Managed Instance | Step 1b (logins), same as on-premises |
| 2, 3, 4 | SQL Server (Standard / Enterprise / Express) | Step 1b (logins) |

Everything from Step 2 onwards is identical on all of them.

---

## Step 1a — Azure SQL Database: contained users

Azure SQL Database has **no `sa`**, and a user database cannot create a server login. Create
*contained* users instead — principals that live entirely inside the database, with their own
passwords, and that travel with it through a restore or a failover.

Connect to **the GymOS database itself** (not `master`) as the server admin:

```sql
CREATE USER gymos_migrator WITH PASSWORD = '<a long random password>';
CREATE USER gymos_app      WITH PASSWORD = '<a different long random password>';
```

> **Prefer Microsoft Entra ID where you can.** A managed identity removes the password from your
> deployment entirely, which is a bigger security win than anything else on this page:
>
> ```sql
> CREATE USER [gymos-api] FROM EXTERNAL PROVIDER;      -- the API's managed identity
> CREATE USER [gymos-migrator] FROM EXTERNAL PROVIDER; -- the deployment pipeline's identity
> ```
>
> The role grants in Step 2 are identical either way. The connection string then carries
> `Authentication=Active Directory Default` and no password. Nothing in the application changes —
> `ConnectionStrings:Database` is read as-is.

**Skip Step 1b.** Go to Step 2.

---

## Step 1b — SQL Server and Managed Instance: logins, then users

Connect to `master` as the administrator:

```sql
CREATE LOGIN gymos_migrator WITH PASSWORD = '<a long random password>', CHECK_POLICY = ON;
CREATE LOGIN gymos_app      WITH PASSWORD = '<a different long random password>', CHECK_POLICY = ON;
```

Then connect to the GymOS database and map them:

```sql
CREATE USER gymos_migrator FOR LOGIN gymos_migrator;
CREATE USER gymos_app      FOR LOGIN gymos_app;
```

> `CHECK_POLICY = ON` enforces the Windows password policy. Two things it rejects that surprise
> people: a password shorter than 8 characters or drawn from fewer than three character classes, and
> **a password containing the login name** — the second is reported with the same "not complex
> enough" message as the first, which is misleading enough that the integration harness hit it and
> spent a while looking at character classes.

---

## Step 2 — grant, in this order

Connected to the GymOS database as the administrator. Identical on every edition.

```sql
-- The migrator changes the schema and applies migrations.
--
-- db_ddladmin rather than db_owner: it carries every CREATE/ALTER/DROP a migration issues, plus
-- ALTER ANY DATASPACE, which `ALTER PARTITION FUNCTION` needs for audit-log partition maintenance.
-- What it does NOT carry is the ability to add members to roles or grant itself more, which is the
-- point of not using db_owner.
ALTER ROLE db_ddladmin   ADD MEMBER gymos_migrator;
ALTER ROLE db_datareader ADD MEMBER gymos_migrator;
ALTER ROLE db_datawriter ADD MEMBER gymos_migrator;

-- Creating and altering security policies is not part of db_ddladmin, and R14's policies are created
-- by migrations. Granted narrowly rather than by promoting the principal to db_owner.
GRANT ALTER ANY SECURITY POLICY TO gymos_migrator;

-- The app reads and writes rows. Nothing else.
ALTER ROLE db_datareader ADD MEMBER gymos_app;
ALTER ROLE db_datawriter ADD MEMBER gymos_app;
```

**Do not add `gymos_app` to `db_ddladmin`, `db_owner` or `db_securityadmin`, and do not grant it
`ALTER ANY SECURITY POLICY` or `CONTROL`.** Each of those is checked at boot and each of them fails
the host with a message naming the problem.

---

## Step 2b — READ_COMMITTED_SNAPSHOT

```sql
-- Azure SQL Database: already ON by default. Verify rather than set:
SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME();

-- SQL Server / Managed Instance: set it, before anything connects.
ALTER DATABASE gymos SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
```

**This is the setting that makes the application's concurrency behave as written**, and it must read
`1` before the API is pointed at the database.

Every handler in GymOS was designed against PostgreSQL, where MVCC gives non-blocking reads for free
and pessimistic locking is something you ask for explicitly. SQL Server's default READ COMMITTED is
the opposite: an ordinary `SELECT` takes shared locks, so two operations that never contended before
can deadlock through a table neither of them locked on purpose. `SetGymFeaturesCommand` and
`SetPlanFeatureCommand` do exactly that — the integration suite has a test for the race, and it
returns a 500 without this setting.

The explicit hints keep meaning what they say: `UPDLOCK` and `REPEATABLEREAD` are still pessimistic
under RCSI. That is the point — the code blocks exactly where it asks to, and nowhere else.

---

## Step 3 — schemas, owned by the migrator

**Before migrations run.** Both reasons are load-bearing:

```sql
CREATE SCHEMA platform AUTHORIZATION gymos_migrator;
CREATE SCHEMA outbox   AUTHORIZATION gymos_migrator;
```

1. **Ownership.** `platform.next_invoice_number` and `platform.ensure_audit_log_partitions` are
   created `WITH EXECUTE AS OWNER`, so the application can trigger the DDL they perform — a new
   year's invoice sequence, next month's partition boundary — without holding DDL rights itself. The
   owner they execute as is *the schema's* owner. A schema created implicitly by the migration would
   belong to whoever ran it, which is the same principal today and silently stops being so the day
   migrations are applied by anything else.
2. **The grant in Step 4** has to name a schema that already exists.

EF Core's `EnsureSchema` is `IF SCHEMA_ID(...) IS NULL`, so creating them here makes the migration's
own attempt a no-op rather than a conflict.

---

## Step 4 — the standing EXECUTE grant

```sql
GRANT EXECUTE ON SCHEMA::platform TO gymos_app;
```

This is the counterpart of the PostgreSQL runbook's `ALTER DEFAULT PRIVILEGES`: one grant, made
before anything exists, covering **every module a future migration adds to the schema**. Without it:

- the first invoice of any year fails loudly, and
- audit-log partition maintenance fails on every boot — non-fatally, at Error level, which is easy to
  miss.

Table and row privileges need no equivalent line. `db_datareader` and `db_datawriter` are
database-wide and already cover every table a future migration creates — which is strictly better
than PostgreSQL's default privileges, since those attached only to objects created *by the role they
were declared for* and silently missed anything created by anyone else.

Then one more, which is not optional:

```sql
GRANT VIEW DEFINITION ON SCHEMA::platform TO gymos_app;
```

**Metadata visibility, and nothing else.** It confers no right to read a row, call a module, or change
anything. It is required because SQL Server's catalog views are permission-filtered, and a security
policy is an object `gymos_app` holds no permission on — so without it,
`SELECT COUNT(*) FROM sys.security_predicates` returns **0** on the application's connection, on a
database where the predicates plainly exist.

That matters because `TenantIsolationValidator` runs on the application's own connection, deliberately:
the question it asks is "is the principal I am about to serve requests as actually filtered", and
asking through a privileged connection would answer a different question. Without this grant the
validator reads its own protection as absent and refuses to boot — failing closed, which is correct,
but for a reason that is not true and with a message that sends you looking for a missing migration.

> **Why this is not in the migration.** It was, briefly, and it failed silently.
> `sys.database_principals` is a security catalog view: it returns only the principals the caller may
> see, and `gymos_migrator` — deliberately not `db_securityadmin` — cannot see `gymos_app` in it. A
> guard of the form `IF EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'gymos_app')` is
> therefore false when the migrator runs it, on a database where that user plainly exists, and the
> GRANT inside it is skipped with no error. The migrator can *perform* the grant; it just cannot
> *ask whether to*.

---

## Step 5 — apply migrations as the migrator

```
ConnectionStrings__Database="Server=<server>.database.windows.net,1433;Database=gymos;User Id=gymos_migrator;Password=<...>;Encrypt=True;Current Language=us_english"
Database__MigrateOnly=true
```

Run the API image with those two settings. It applies migrations and exits; the exit code is
load-bearing, so gate the deployment on it.

**`Current Language=us_english` is not cosmetic.** `UnitOfWork` recovers the name of a violated
unique index by parsing the server's error message, because `SqlException` exposes it nowhere else,
and SQL Server localises that message per login language. `SetGymFeaturesCommand` retries on exactly
one constraint name and must not retry on any other.

**No `TrustServerCertificate`.** Azure SQL presents a certificate chaining to a public CA, so
`Encrypt=True` alone verifies properly. Setting `TrustServerCertificate=True` would give you an
encrypted channel to an unauthenticated server, which is the development sidecar's compromise and has
no place here.

---

## Step 6 — verify, before pointing the API at it

Connected **as `gymos_app`**, not as the administrator:

```sql
SELECT USER_NAME()                                                                    AS principal,
       ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0)                                        AS is_sysadmin,
       ISNULL(IS_ROLEMEMBER('db_owner'), 0)                                           AS is_db_owner,
       ISNULL(IS_ROLEMEMBER('db_ddladmin'), 0)                                        AS is_db_ddladmin,
       ISNULL(IS_ROLEMEMBER('db_securityadmin'), 0)                                   AS is_db_securityadmin,
       ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY SECURITY POLICY'), 0)          AS can_alter_policies,
       ISNULL(HAS_PERMS_BY_NAME(NULL, NULL, 'CONTROL'), 0)                            AS has_control;
```

**Every column after `principal` must be 0.** These are exactly the checks
`TenantIsolationValidator` runs on each boot, so anything non-zero here is a host that will refuse to
start — better discovered now than at deploy time.

> `IS_SRVROLEMEMBER` returns NULL on Azure SQL Database, where server roles do not exist for a
> contained user; `ISNULL(..., 0)` reads that as "no". Nothing is lost — `db_owner`, checked on its
> own line, is Azure's nearest equivalent.

Then confirm the app can actually work:

```sql
-- Should succeed: the standing grant from Step 4.
DECLARE @n bigint;
EXEC platform.next_invoice_number @year = 2026, @number = @n OUTPUT;
SELECT @n AS issued_number;

-- Should FAIL with "CREATE TABLE permission denied". If it succeeds, Step 2 went wrong.
CREATE TABLE platform.should_not_exist (id int);
```

---

## Step 7 — point the API at it

```
ConnectionStrings__Database="Server=<server>.database.windows.net,1433;Database=gymos;User Id=gymos_app;Password=<...>;Encrypt=True;Current Language=us_english"
Database__MigrateOnStartup=false
Database__MigrateOnly=false
```

`MigrateOnStartup=false` because `gymos_app` holds no DDL — turning it on produces a failed migration
on every boot, not a migrated database.

---

## Known follow-up: transient-fault retry

`AddPlatformPersistence` deliberately does **not** call `EnableRetryOnFailure`, and on Azure SQL that
is a gap worth closing rather than a settled decision. Azure SQL disconnects clients routinely —
failover, scaling, throttling — and without a retry strategy each one surfaces as a request failure.

It cannot simply be switched on. `SqlServerRetryingExecutionStrategy` refuses user-initiated
transactions outright, and `IUnitOfWork.BeginTransactionAsync` is exactly that;
`PlatformIdentitySeeder` and `FeatureCatalogSeeder` both open one on every boot, so enabling it as-is
fails startup. Adopting it means wrapping every `BeginTransactionAsync` call site in
`Database.CreateExecutionStrategy().ExecuteAsync(…)`, which makes those delegates replayable — a
change to transaction semantics, and therefore a change with its own tests rather than a
configuration flag.

---

## Rotating a password later

```sql
-- Azure SQL Database (contained user), connected to the GymOS database:
ALTER USER gymos_app WITH PASSWORD = '<new>' OLD_PASSWORD = '<old>';

-- SQL Server / Managed Instance (login), connected to master:
ALTER LOGIN gymos_app WITH PASSWORD = '<new>';
```

Then update the deployment's secret and restart. The two principals rotate independently, which is
half the point of having two: rotating the app's credential does not touch the one that can change
the schema.
