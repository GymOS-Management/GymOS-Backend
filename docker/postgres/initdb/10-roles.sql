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
--
-- ── Why \getenv rather than shell string-substitution ─────────────────────────────────────────
-- The entrypoint runs this file as `psql -f 10-roles.sql`, with no `-v` bindings of its own
-- (docker_process_sql in docker-entrypoint.sh), so `:'migrator_password'` is not populated by
-- magic. Two ways to fix that were considered:
--
--   * A `.sh` wrapper that shell-interpolates the password into a `psql -v name=value -f -`
--     invocation. Rejected: a password containing a single quote would either break the quoting
--     or, worse, close the argument early and let the rest be interpreted as something else —
--     exactly the class of bug row-level security exists to contain, reintroduced at the one
--     script that provisions it.
--   * `\getenv`, a psql meta-command (present in this image's bundled psql 17.10) that reads an
--     environment variable straight into a psql variable, entirely inside psql's own parser. No
--     shell is involved in the value at all.
--
-- Verified directly against `postgres:17-alpine` before choosing this: with `\getenv foo MYVAR`
-- and `MYVAR` set to `pa'ss` (a value containing a single quote), `SELECT :'foo';` returned the
-- literal three-character value `pa'ss` — psql escaped the embedded quote itself when substituting
-- into the `:'...'` form, so the value can never terminate the string early or inject SQL. The same
-- run also confirmed the fail-closed side: a `:'var'` reference to a variable that was never set
-- (no `\getenv`, no `--set`) is a hard `ERROR: syntax error at or near ":"` under `ON_ERROR_STOP=1`
-- (which docker_process_sql always passes) — so a missing `GYMOS_MIGRATOR_PASSWORD` or
-- `GYMOS_APP_PASSWORD` aborts initialisation instead of silently creating a role with no password.

\getenv migrator_password GYMOS_MIGRATOR_PASSWORD
\getenv app_password GYMOS_APP_PASSWORD
\getenv database_name POSTGRES_DB

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
--
-- DUPLICATED, unavoidably, in tests/GymOS.IntegrationTests/Tenancy/TenantIsolationTests.cs
-- (CreateRolesSql): an initdb script mounted into a container and a Testcontainers fixture cannot
-- share a SQL file. Change these two statements and you must change that copy in the same commit —
-- if they drift, ATableCreatedByTheMigratorIsUsableByTheAppRoleWithNoExplicitGrant keeps passing
-- against the suite's own grants while production breaks on the first table a future migration adds.
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gymos_app;
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT USAGE ON SCHEMAS TO gymos_app;

-- No sequence grants: GymOSModelConventions.ApplyClientAssignedKeys makes every key client-assigned,
-- so the database generates nothing.
