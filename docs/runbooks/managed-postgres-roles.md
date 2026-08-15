# Provisioning the three GymOS roles on a managed PostgreSQL instance

**Applies to:** Azure Database for PostgreSQL Flexible Server (AD-11), or any managed instance where
you administer through a login rather than the container. **Not applicable to the Docker deployment**
— there, `docker/postgres/initdb/10-roles.sql` does this automatically, once, on an empty volume. This
runbook exists because a managed instance has no `/docker-entrypoint-initdb.d`: nobody runs an
`.sql` file for you on first boot, so the same three roles have to be created here by an operator,
by hand, once, as ordered SQL.

**Status:** documented, not provisioned. Nothing on Azure exists yet for GymOS — there is no deployment
to run this against today. This is recorded ahead of that day so whoever provisions the first managed
instance has the ordered statements rather than a blank cursor, and so the shape matches
`10-roles.sql` and `docs/BusinessRules/TenantIsolation.md` rather than being reinvented in isolation.
Adapted from, not transcribed from, the appendix of
`docs/superpowers/specs/2026-08-15-database-roles-design.md` — that appendix assumes a database that
already has objects on it; the ordering and the "why" below are spelled out for an operator who has
never run this before, which the appendix does not attempt.

Run everything in this document connected as the instance's administrator (Azure calls this the
**server admin login** — the one you supplied when the server was created). Every statement below is
plain SQL over any `psql`-compatible client; nothing here needs a shell, an extension, or a
`/docker-entrypoint-initdb.d` equivalent.

## Step 0 — check what you actually are, before assuming anything

**Do this first, before creating a single role.** Azure's administrator login is **not** a PostgreSQL
superuser — Azure deliberately withholds `SUPERUSER` from every login, including the administrator,
because the underlying host is shared infrastructure Microsoft manages. That part is documented and
safe to assume. **What is not safe to assume is `BYPASSRLS`** — whether the administrator role can see
through row-level security regardless of policy — because that attribute is not part of Azure's public
contract and this runbook does not know your instance's configuration. Check both, on the role you are
about to use, before doing anything else:

```sql
SELECT rolname, rolsuper, rolbypassrls
FROM pg_roles
WHERE rolname = current_user;
```

You are looking for `rolsuper = false`. If it reads `true`, stop — this is not a managed instance shaped
the way this runbook assumes, and the reasoning below about "the administrator can't bypass RLS anyway"
does not hold. `rolbypassrls` matters for a different reason: it tells you whether *this* login could
stand in as an ad hoc replacement for `gymos_app` in an emergency (it cannot, if isolation matters,
regardless of the answer — see below) and whether anything you run manually against tenant-scoped
tables as this login will itself be filtered. Record what you saw; do not proceed on the assumption
that "Azure's admin is basically safe" without having read the actual row.

**Why this matters even though the administrator is never the runtime role.** The whole point of the
two-role split (`docs/BusinessRules/TenantIsolation.md`, "Why two non-superuser roles, not one") is that
nobody who serves a request owns anything or can bypass a policy. If the administrator role turns out to
carry `BYPASSRLS`, that is merely true of *the administrator* — it does not weaken `gymos_app` or
`gymos_migrator`, which you are about to create as plain `NOSUPERUSER NOBYPASSRLS` logins regardless of
what the administrator is. But it does mean *you*, running the statements in this document, are not
subject to the policies you are about to install, and any manual query you run afterwards through this
same login proves nothing about whether isolation actually holds. Test isolation as `gymos_app`, never
as the administrator.

**R14's validator is the backstop here, not the plan.** `TenantIsolationValidator` refuses to boot the
API against any connected role reporting `rolsuper` or `rolbypassrls` — unconditionally, on every boot,
regardless of what this runbook got right or wrong. If every step below is followed correctly, that
check never fires and the API simply starts. If a connection string in the eventual Azure deployment
ends up pointing at the wrong role, the check *will* fire, loudly, and the API will not come up — which
is the correct failure, but it is meant to be the safety net under this runbook, not a substitute for
reading Step 0's query result with your own eyes before you deploy anything against it.

## Step 1 — create the two roles

Two passwords, generated for this instance — not the development defaults `docker-compose.override.yml`
uses, and not shared between the two roles:

```sql
CREATE ROLE gymos_migrator LOGIN PASSWORD '<generate one>' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE gymos_app      LOGIN PASSWORD '<generate one>' NOSUPERUSER NOBYPASSRLS;
```

Same attributes as the Docker deployment's roles, same reasoning
(`docs/BusinessRules/TenantIsolation.md` §"The three database roles" and "Why two non-superuser roles,
not one"): `gymos_migrator` is going to own the database and run DDL; `gymos_app` is going to own
nothing and serve requests, so an injected `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` has nothing to
run as.

## Step 2 — hand over ownership

This is where a managed instance genuinely differs from the Docker deployment, and the reason this
step exists as its own section rather than being folded into Step 1: **`10-roles.sql` runs against an
empty data directory**, so there is nothing yet to own — it creates the roles and default privileges
before a single table exists. A managed instance provisioned this way, by contrast, typically already
has a database, and very possibly already has schemas and tables in it (migrated once by an
administrator connection, or inherited from an earlier deployment). Ownership has to be **transferred**
to `gymos_migrator`, not merely declared for the future.

```sql
-- Move every object the administrator currently owns to gymos_migrator. Run this connected as
-- whichever role currently owns the database's objects — typically the administrator login itself.
REASSIGN OWNED BY CURRENT_USER TO gymos_migrator;

-- The database and its schemas are not "owned objects" in the sense REASSIGN OWNED covers — they
-- need their own ALTER. Adjust the schema list to whatever this instance actually has; `platform` is
-- GymOS's own schema, `public` is PostgreSQL's default one and usually still exists alongside it.
ALTER DATABASE gymos OWNER TO gymos_migrator;
ALTER SCHEMA public   OWNER TO gymos_migrator;
ALTER SCHEMA platform OWNER TO gymos_migrator;
```

Then declare the same default privileges `10-roles.sql` declares, so every table `gymos_migrator`
creates **from now on** is automatically readable and writable by `gymos_app` with no per-table grant:

```sql
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gymos_app;
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT USAGE ON SCHEMAS TO gymos_app;
```

**Stop here and read this before considering the instance ready.** Default privileges are exactly
that — *default* — and PostgreSQL is explicit that they never reach backward: a default privilege
declared today changes nothing about a table that already exists, only about one created after the
declaration runs. If this instance already has tables (from an earlier migration, an earlier
deployment, anything predating this runbook), the two statements above cover none of them. The
symptom, if you skip this, is not an error that mentions privileges — it is `gymos_app` getting a
permission-denied on the very first query against an existing table, indistinguishable at a glance from
a broken connection string or a typo in the schema name. This is the mistake people make on exactly
this step, so it gets its own explicit grant rather than being left to default privileges to (not)
cover:

```sql
-- Reaches every table that already exists in platform, which the two ALTER DEFAULT PRIVILEGES
-- statements above do not. Re-run this line (harmlessly — GRANT is idempotent) after any migration
-- that runs directly against this instance outside the usual migrator path, and after this
-- one-time ownership transfer specifically.
GRANT USAGE ON SCHEMA platform TO gymos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform TO gymos_app;
```

If `public` also holds tables you expect `gymos_app` to read, repeat the last `GRANT` for `public` too.
No sequence grants are needed for either schema: `GymOSModelConventions.ApplyClientAssignedKeys` makes
every primary key client-assigned, so the database generates nothing from a sequence.

## Step 3 — point the deployment at the new roles

Nothing else in this runbook changes application behaviour by itself — the roles exist and own what
they should, but the API and the migrator still have to actually connect as them. That is the same
connection-string shape the Docker deployment already uses (`docker-compose.yml`,
`docker-compose.prod.yml`): the migrator's `ConnectionStrings__Database` names `gymos_migrator`, the
API's names `gymos_app`, and neither ever names the administrator login. `POSTGRES_SSL_MODE=Require` (or
stronger) belongs on both, since a managed instance is reached over the network rather than a Docker
bridge.

## Verifying before you trust it

Confirm the roles landed the way Step 1 and Step 2 intended, still connected as the administrator:

```sql
SELECT rolname, rolsuper, rolbypassrls
FROM pg_roles
WHERE rolname IN ('gymos_migrator', 'gymos_app');
-- expect: both rows rolsuper = false, rolbypassrls = false

SELECT datname, pg_get_userbyid(datdba) AS owner
FROM pg_database
WHERE datname = 'gymos';
-- expect: owner = gymos_migrator
```

Then let the application prove the rest. Deploy the migrator first and confirm it exits `0`; deploy the
API and confirm `TenantIsolationValidator` passes at boot rather than refusing it. A refusal at this
point means Step 0 through Step 3 above were not followed exactly as written for this instance — go
back to Step 0's query and re-check what the connecting role actually is before assuming the validator
is wrong.
