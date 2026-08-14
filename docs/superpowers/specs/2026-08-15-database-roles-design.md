# A non-superuser database role, so R14's isolation actually filters

**Status:** approved 2026-08-15, not yet built.
**Implements:** the deployment half of **R14** (tenant isolation). R14's mechanism shipped on
2026-08-14 and currently has no effect, because the application connects as a PostgreSQL superuser.
**Revisits:** D5 of `2026-08-14-r14-tenant-isolation-design.md`, which deferred a separate non-owner
runtime role. That deferral rested on a premise this increment corrects.
**Blocks:** the Members domain. R14's validator refuses to boot on a bypassing role once any entity
implements `ITenantScoped`, and `Member` is the first.

## 1. Why this exists

R14 installed row-level security: policies on every tenant-scoped table, `FORCE ROW LEVEL SECURITY`
so the table owner is not exempt, a startup validator that asserts all of it, and twelve integration
tests. None of it filters a single row in the Docker deployment, because
`docker-compose.yml` sets `POSTGRES_USER: gymos`, every connection string connects as `gymos`, and
`gymos` is the bootstrap superuser the `postgres` image creates. **PostgreSQL exempts a superuser
from row-level security entirely — `FORCE` does not constrain one.**

R14's D5 rejected a separate non-owner role on the grounds that it "splits the connection string in
two, complicates migrations and the Docker compose story, and buys little once `FORCE` is asserted at
boot". The first three costs are real and this design pays them. The fourth is false twice over:
`FORCE` buys nothing against a superuser, and even against a correct non-superuser *owner* it can be
switched off in one statement by anything that can execute SQL as that owner.

The R14 increment's own test suite is the proof this is not theoretical: it had to create a
`NOSUPERUSER NOBYPASSRLS` role of its own before any of its six isolation tests meant anything. Run as
the container's bootstrap role, every one of them reported "no isolation" regardless of the policies.

## 2. Scope

### In

- Three database roles, replacing today's single superuser (§3)
- `docker/postgres/initdb/10-roles.sql`, creating them and setting default privileges
- A one-shot `migrator` compose service, and a `Database:MigrateOnly` host mode
- Connection-string changes across `docker-compose.yml`, `docker-compose.override.yml`,
  `docker-compose.prod.yml` and `appsettings.Development.json`
- Two additions to `TenantIsolationValidator`: the connected-role check becomes unconditional, and it
  asserts the connected role does not own the tenant-scoped tables it checks
- The integration harness moves off the superuser (§6) — the largest single piece of work
- `docs/BusinessRules/TenantIsolation.md` and `STATUS.md` updates; an Azure runbook

### Out, and why

| Not building | Why |
|---|---|
| Managed Azure provisioning | Nothing is provisioned yet and there is no deployment doc. A documented runbook, enforced by the boot check, is honest; building and testing machinery for an environment that does not exist is not. |
| Ownership transfer for existing databases (`REASSIGN OWNED`) | Every database is disposable and will be wiped. The procedure is recorded as an appendix for whoever eventually needs it, not built or tested. |
| Credential rotation | The current password is a known-public development value. Any real credential that exists elsewhere is the maintainer's to rotate. |
| Any change to the policies themselves | R14's SQL is unchanged. This increment changes only *who connects*. |
| A per-tenant or per-request database role | Enormously stronger and enormously more expensive; the session-variable approach R14 chose is the one the policies are written against. |

## 3. The roles

| Role | Attributes | Owns | Used by |
|---|---|---|---|
| `gymos_admin` | superuser — this is `POSTGRES_USER` | the cluster | humans, `psql`, maintenance |
| `gymos_migrator` | `NOSUPERUSER NOBYPASSRLS` | database `gymos`, its schemas, every table | the `migrator` service only |
| `gymos_app` | `NOSUPERUSER NOBYPASSRLS` | nothing | the API at runtime |

`POSTGRES_USER` is renamed from `gymos` to `gymos_admin`. Volumes are being wiped, so this is free,
and it removes the trap where the account the application used to use still answers to the name
everyone's fingers reach for. `POSTGRES_DB` stays `gymos`.

### D1 — The migrator owns; the app owns nothing

The role that runs DDL and the role that serves requests are different roles.

An owner can execute `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` and switch its own isolation off in
one statement. Row-level security is largely a defence against SQL injection, and an injected
statement running as an owner can therefore disable the very control meant to contain it — with the
validator only noticing at the next boot, potentially weeks later. `gymos_app` owns nothing and holds
no DDL rights, so the same injected statement is refused by PostgreSQL.

This is the whole reason for the split, and §7 tests it directly rather than assuming it.

### D2 — Privileges by default privileges, not per-table grants

```sql
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gymos_app;
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT USAGE ON SCHEMAS TO gymos_app;
```

Deliberately with **no `IN SCHEMA` clause**. The `platform` schema is created by a migration, so an
`IN SCHEMA platform` form would have to run after migrations — by which point it would not cover the
tables those migrations had just created. Declared globally, before any migration runs, every future
table and schema is covered automatically, and a new module cannot ship a table the application
silently cannot read.

**The constraint this creates, which belongs in the business rules:** default privileges attach to
objects created *by `gymos_migrator`*. Anything that ever creates a table as another role produces a
table `gymos_app` cannot touch. Today only the migrator holds DDL rights, so the constraint holds by
construction — but it is the kind of thing that stops holding quietly.

No sequence grants are needed: `GymOSModelConventions.ApplyClientAssignedKeys` makes every key
client-assigned, so the database generates nothing.

### D3 — `FORCE ROW LEVEL SECURITY` stays

It is now belt to `gymos_app`'s braces, since policies apply to a non-owner regardless. It still
governs `gymos_migrator`, which does own the tables, so removing it would mean the migrator's
connection reads across every tenant. R14's validator continues to assert `relforcerowsecurity`
unchanged.

## 4. Migrations

`Database:MigrateOnStartup` becomes `false` everywhere, development included, and a one-shot
`migrator` compose service applies migrations before the API starts.

Rather than adding `dotnet ef` or a migration bundle to the runtime image, the host gains a
`Database:MigrateOnly` mode: apply migrations, exit, never start the web server. The `migrator`
service is then the same image with the migrator connection string and that one flag; `api` declares
`depends_on: { migrator: { condition: service_completed_successfully } }`.

**It must exit non-zero when migrations fail.** If it exits 0 on failure, `api` starts against an
unmigrated database, and R14's validator hits its pending-migrations guard and switches the entire
control off for that boot — logged as a warning, but the platform serves. That is the exact shape this
project keeps finding, so it is called out here rather than left to the implementer.

This splits responsibilities cleanly: **the migrator does DDL, the app does data.** Seeders and the
R14 validator stay in the API's startup path as `gymos_app`, which is correct — `gymos_app` is the role
whose isolation is worth validating, and seeding is DML.

Development keeps its "one command" property: `docker compose up` still brings up a working stack from
an empty volume, with one short extra step in the middle.

For a bare `dotnet run` against a local PostgreSQL — a real workflow, since
`appsettings.Development.json` points at `localhost:5432` — migrations are a documented
`dotnet ef database update --connection …` with the migrator credentials, recorded in `STATUS.md`'s
fresh-machine setup.

## 5. Secrets in compose

Two new passwords, following the pattern `docker-compose.yml` already documents: an empty default in
`docker-compose.yml` (because compose interpolates each file independently, a `${VAR:?}` marker there
aborts even when an overlay supplies the value), a known-public default in
`docker-compose.override.yml`, and `${VAR:?}` guards in `docker-compose.prod.yml`.

CI's existing "production overlay refuses to run without secrets" step therefore keeps working, and
now covers the new credentials too.

The `GYMOS_DB_CONNECTION` escape hatch in the override file — which points the API at an external
shared database — continues to work, and now names `gymos_app` in its default.

## 6. The integration harness

Making the connected-role check unconditional means it runs in every integration test host, and
Testcontainers' bootstrap role is a superuser. Left alone, all 303 integration tests would fail to
boot.

So `PostgresFixture` provisions a non-superuser role per test database and hosts connect as it. This
is the largest and riskiest piece of the increment, touching a suite of 303 passing tests, and it gets
its own task first so that it is isolated and revertible rather than tangled with the role SQL.

**Why do it rather than keep the check conditional.** "The tests run as a superuser and the deployment
does not" is precisely the divergence that made R14's isolation tests prove less than they appeared
to — that suite had to build its own restricted role before any of its assertions meant anything.
Leaving the rest of the harness on a superuser keeps the trap loaded for the next security-sensitive
test somebody writes. The failure mode if this is the wrong call is loud and immediate: tests do not
boot.

Most hosts need only a single non-superuser role that owns its own test database — enough to satisfy
the role check, and cheap. That role owns its tables, so those hosts keep migrating themselves exactly
as they do today; only the credentials change. The tenancy suite goes to the full two-role shape,
because it is the one asserting the ownership separation.

**That is itself a divergence from the deployment, and it deserves naming rather than glossing.** The
deployment separates migrator from app; most test hosts will not. The justification is that §7's test 4
inspects only *tenant-scoped* tables, and outside the tenancy suite nothing is tenant-scoped — so those
hosts do not exercise the separation either way, and making 300 tests carry a two-role setup to prove
nothing would be cost without return. The line to hold is therefore: **any suite that touches a
tenant-scoped table uses the full two-role shape.** Members' tests land on the far side of that line.
If the rule starts being bent, the harness has drifted back to exactly where R14 found it.

## 7. Testing

| # | Test | What would otherwise hide |
|---|---|---|
| 1 | **`gymos_app` cannot disable its own isolation** — `ALTER TABLE … NO FORCE ROW LEVEL SECURITY` on the app connection is refused | The entire reason for choosing a two-role split |
| 2 | The same statement **succeeds** on the migrator connection | That test 1 fails for a privilege reason and not an unrelated one |
| 3 | A superuser host fails the boot **with nothing tenant-scoped in the model** | The check's new unconditionality |
| 4 | A tenant-scoped table owned by the connected role fails the boot | That the migrator/app separation is enforced, not conventional |
| 5 | A table created by `gymos_migrator` after bootstrap is readable and writable by `gymos_app` with no explicit grant | D2's default privileges silently not applying — which in production is a permission error on a brand-new table |
| 6 | The stack comes up and is healthy | The new migrator service's ordering (CI's existing `docker` job) |

Tests 1 and 2 are a pair and neither is meaningful alone. Test 5 is the one most likely to be subtly
wrong.

**Every test above must be watched failing before its fix exists.** This project has shipped five
guards that could not exhibit the failure they guarded, the most recent found during R14 itself and
only by running the experiment rather than reasoning about it.

## 8. Definition of done

- All six tests green; full integration suite green at its new count; `dotnet build` 0 warnings,
  `dotnet format` clean
- `docker compose down -v && docker compose up` produces a working stack from an empty volume
- The API runs as `gymos_app`, owns nothing, and R14's validator passes on every boot
- `docs/BusinessRules/TenantIsolation.md` records the roles, D2's constraint, and the Azure runbook;
  `STATUS.md`'s open item for the superuser role is closed and its fresh-machine setup updated
- No change to any RLS policy

## 9. Risks

- **The harness change destabilises 303 passing tests.** Sequenced first and alone, so it fails in
  isolation. Mitigated further by the failure being a boot error rather than a subtle behaviour change.
- **Default privileges are order-sensitive and easy to get subtly wrong** (§D2). Test 5 exists for
  this; the failure would otherwise appear only when a future migration adds a table.
- **`MigrateOnly` is new code on the startup path**, and the startup path is where this project has
  already found a control that could be silently skipped. Its exit code is the load-bearing part.
- **The renamed `POSTGRES_USER` invalidates any `.env` a developer already has**, and the healthcheck
  references it. A stale `.env` produces a confusing first-boot failure; the wipe instruction should
  say so explicitly.
- **Azure's administrator is not a superuser but its `BYPASSRLS` status is unverified.** The runbook
  must have whoever provisions it check `rolsuper` and `rolbypassrls` on the role the application will
  use, rather than assuming. R14's validator will refuse the boot if they are wrong, which is the
  right direction, but the runbook should not let it get that far.

## Appendix — Transferring an existing database

Not built or tested; recorded for whoever needs it. Every current database is being wiped.

```sql
CREATE ROLE gymos_migrator LOGIN PASSWORD '…' NOSUPERUSER NOBYPASSRLS;
CREATE ROLE gymos_app      LOGIN PASSWORD '…' NOSUPERUSER NOBYPASSRLS;

REASSIGN OWNED BY gymos TO gymos_migrator;
ALTER DATABASE gymos OWNER TO gymos_migrator;
ALTER SCHEMA public   OWNER TO gymos_migrator;
ALTER SCHEMA platform OWNER TO gymos_migrator;

ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO gymos_app;
ALTER DEFAULT PRIVILEGES FOR ROLE gymos_migrator
    GRANT USAGE ON SCHEMAS TO gymos_app;

-- Default privileges do not reach objects that already exist, so existing tables need this too.
GRANT USAGE ON SCHEMA platform TO gymos_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA platform TO gymos_app;
```

The `GRANT … ON ALL TABLES` line is the one people forget, and its symptom is a permission error on
every query rather than anything that points at privileges.
