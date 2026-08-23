# SQL Server initialisation

Two scripts, applied once against an empty instance by the `db-init` service in
`docker-compose.yml`, before the `migrator` service runs:

| Script | What it does |
| --- | --- |
| `00-database.sql` | Creates the `gymos` database with a fixed collation. |
| `10-principals.sql` | Creates `gymos_migrator` and `gymos_app`, their roles, the two schemas, and the standing `EXECUTE` grant. |

They run in filename order and are **idempotent** — every statement is guarded — so re-running
them against an initialised instance is a no-op. That matters because, unlike the PostgreSQL
scripts these replace, they are not tied to the data directory being empty.

## Why a service and not an entrypoint hook

The `postgres` image runs anything in `/docker-entrypoint-initdb.d` the first time it initialises a
data directory. **The SQL Server image has no equivalent.** There is no hook, no ordering guarantee,
and no "first boot" signal — the container simply starts an engine. So initialisation is an
explicit one-shot container that waits for the engine to report healthy, runs these scripts as
`sa`, and exits. `migrator` gates on it completing successfully, and `api` gates on `migrator`.

The trade this makes is visible and intentional: initialisation is a step in the dependency graph
rather than a side effect of an empty volume, which is why it has to be idempotent and why
`docker compose up -d` still works against a stack that is already running.

## Credentials

`sa` exists only for these two scripts and for a human debugging the container. Neither the API nor
the migrator ever connects as it — see the comment at the top of `10-principals.sql` for why that
is a correctness requirement and not just hygiene.

Development passwords are public, committed, and identical for every developer, exactly as the
PostgreSQL setup's were. `docker-compose.prod.yml` supplies its own through `${VAR:?}` markers and
refuses to start without them. **Never put a production credential in a tracked file.**

SQL Server enforces password complexity on `CREATE LOGIN` (`CHECK_POLICY = ON`), so the development
values are longer and mixed-case where the PostgreSQL ones were not. That is the server's rule
rather than a change of policy.

## Applying the same setup to a managed instance

Azure SQL Database has no `sa` and no server-scoped `CREATE LOGIN` in a user database, so these
scripts do not run there unchanged. `docs/runbooks/managed-sqlserver-roles.md` carries the
contained-user equivalent, statement for statement, with the differences called out.
