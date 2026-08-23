-- The two principals the platform runs as, and what each may do. Runs once, as sa, before any
-- migration.
--
-- The application does not connect as sa, and not because sa is merely "too powerful" in the
-- abstract: `sysadmin` and `db_owner` hold ALTER ANY SECURITY POLICY, so a connection using either
-- can execute `ALTER SECURITY POLICY ... WITH (STATE = OFF)` and switch R14's tenant isolation off
-- in one statement. TenantIsolationValidator refuses to boot a host connected as such a principal.
--
-- Two principals, because owning a schema and reading its tables are different privileges:
--
--   gymos_migrator  owns the schemas, runs DDL, applies migrations. Used only by the one-shot
--                   `migrator` service.
--   gymos_app       owns nothing, holds no DDL rights, serves requests.
--
-- ── What SQL Server needed that PostgreSQL did not ───────────────────────────────────────────
-- The PostgreSQL original was `docker/postgres/initdb/10-roles.sql`, dropped into the image's
-- /docker-entrypoint-initdb.d. The SQL Server image has no such directory and runs no
-- initialisation scripts at all, so this is invoked by the `db-init` one-shot service in
-- docker-compose.yml instead. Same "runs once against an empty instance" contract, made explicit.
--
-- Passwords arrive through sqlcmd :setvar bindings rather than shell interpolation, for the reason
-- the PostgreSQL version used psql's \getenv: a password containing a quote must not be able to
-- terminate the string it sits in and have the remainder read as SQL. That is the class of bug
-- row-level security exists to contain, and it must not be reintroduced by the script that
-- provisions it.

USE [$(GYMOS_DB)];
GO

-- ── Logins (server scope) ────────────────────────────────────────────────────────────────────
IF SUSER_ID(N'gymos_migrator') IS NULL
    EXEC sys.sp_executesql
        N'CREATE LOGIN gymos_migrator WITH PASSWORD = @p, CHECK_POLICY = ON',
        N'@p sysname', @p = N'$(GYMOS_MIGRATOR_PASSWORD)';
GO
IF SUSER_ID(N'gymos_app') IS NULL
    EXEC sys.sp_executesql
        N'CREATE LOGIN gymos_app WITH PASSWORD = @p, CHECK_POLICY = ON',
        N'@p sysname', @p = N'$(GYMOS_APP_PASSWORD)';
GO

-- ── Users (database scope) ───────────────────────────────────────────────────────────────────
IF DATABASE_PRINCIPAL_ID(N'gymos_migrator') IS NULL
    CREATE USER gymos_migrator FOR LOGIN gymos_migrator;
GO
IF DATABASE_PRINCIPAL_ID(N'gymos_app') IS NULL
    CREATE USER gymos_app FOR LOGIN gymos_app;
GO

-- ── gymos_migrator: changes the schema ───────────────────────────────────────────────────────
-- db_ddladmin rather than db_owner, and the difference matters: db_ddladmin carries
-- ALTER ANY DATASPACE (which `ALTER PARTITION FUNCTION` needs) and every CREATE/ALTER/DROP a
-- migration issues, but not the ability to add members to roles or to grant itself more. It also
-- reads and writes rows, because seed data is applied by migrations' own code paths.
ALTER ROLE db_ddladmin   ADD MEMBER gymos_migrator;
ALTER ROLE db_datareader ADD MEMBER gymos_migrator;
ALTER ROLE db_datawriter ADD MEMBER gymos_migrator;
GO

-- Creating and altering security policies is not part of db_ddladmin, and R14's policies are
-- created by migrations. Granted narrowly rather than by promoting the principal to db_owner.
GRANT ALTER ANY SECURITY POLICY TO gymos_migrator;
GO

-- ── gymos_app: reads and writes rows, and nothing else ───────────────────────────────────────
-- Deliberately NOT db_ddladmin, db_owner, or db_securityadmin, and holding neither
-- ALTER ANY SECURITY POLICY nor CONTROL. TenantIsolationValidator asserts every one of those on
-- each boot and refuses to start if any is true, so this block and that check must agree.
ALTER ROLE db_datareader ADD MEMBER gymos_app;
ALTER ROLE db_datawriter ADD MEMBER gymos_app;
GO

-- ── Schemas ──────────────────────────────────────────────────────────────────────────────────
-- Created here rather than left to EF Core's EnsureSchema, for two reasons that are not stylistic:
--
--   1. OWNERSHIP. platform.next_invoice_number and platform.ensure_audit_log_partitions are
--      created WITH EXECUTE AS OWNER so the application can trigger the DDL they perform (a new
--      year's sequence, next month's partition boundary) without holding DDL rights itself. The
--      owner they execute as is the schema's owner, so the schema must belong to gymos_migrator.
--      A schema created implicitly by the migration would belong to whoever ran it, which is the
--      same principal today and would silently stop being so the day migrations are applied by
--      anything else.
--   2. THE STANDING GRANT BELOW has to name a schema that already exists.
--
-- EF's EnsureSchema is `IF SCHEMA_ID(...) IS NULL`, so it becomes a no-op rather than a conflict.
IF SCHEMA_ID(N'platform') IS NULL EXEC(N'CREATE SCHEMA platform AUTHORIZATION gymos_migrator');
GO
IF SCHEMA_ID(N'outbox') IS NULL EXEC(N'CREATE SCHEMA outbox AUTHORIZATION gymos_migrator');
GO

-- ── The standing EXECUTE grant ───────────────────────────────────────────────────────────────
-- The counterpart of the PostgreSQL script's ALTER DEFAULT PRIVILEGES: a grant made once, before
-- anything exists, that covers every module a future migration adds to the schema. Without it the
-- first invoice of a year fails loudly and audit partition maintenance fails on every boot.
--
-- It cannot be issued from the migration that creates those modules, even though that is where
-- they are defined. sys.database_principals is a security catalog view and returns only principals
-- the caller may see; gymos_migrator cannot see gymos_app in it, so a guarded GRANT there is
-- skipped without error on a database where the user plainly exists. Measured, not assumed — see
-- the RuntimeDatabaseProgrammability migration's remarks.
--
-- Table and row privileges need no equivalent line: db_datareader and db_datawriter are
-- database-wide and already cover every table a future migration creates.
GRANT EXECUTE ON SCHEMA::platform TO gymos_app;
GO

-- ── Deliberately absent ──────────────────────────────────────────────────────────────────────
-- No extensions, no CLR, no TRUSTWORTHY. The PostgreSQL setup had a 00-extensions.sql that
-- installed nothing and said why; the same reasoning applies and needs even less here, since the
-- three things GymOS would have reached for are all built in: NEWID() for identifiers (unused —
-- ids are client-assigned), HASHBYTES for digests (unused — Argon2id runs in the application), and
-- native JSON handling on nvarchar(max).
--
-- TRUSTWORTHY in particular stays OFF. EXECUTE AS OWNER on the two runtime modules needs only
-- database-scoped rights, so nothing here requires the database to be trusted with server-scoped
-- impersonation, and turning it on would be a standing privilege-escalation path.
