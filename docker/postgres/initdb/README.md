# PostgreSQL initialisation scripts

Files here are executed by the `postgres` image's entrypoint **once**, in filename order, and
**only when the data directory is empty**. That last part is the thing that catches people out:

- adding a script and running `docker compose restart postgres` does nothing;
- adding a script and running `docker compose up -d` on an existing volume does nothing;
- the only way to re-run them is to destroy the volume (`docker compose down -v`), which destroys
  the database with it.

So these scripts are for cluster-level setup that must exist before anything else — extensions,
roles, database-wide settings. They are **not** where schema goes.

## Why the schema is not here

GymOS owns its schema in EF Core migrations (`src/Platform/GymOS.Platform.Infrastructure/
Persistence/Migrations`), which are versioned, reviewed, and applied identically to a laptop, CI,
and a managed Azure database that has no `/docker-entrypoint-initdb.d` at all. A table created here
would exist only in containers, would not appear in the model snapshot, and would make
`dotnet ef migrations has-pending-model-changes` disagree with reality.

Rule of thumb: if a managed database service could not run it for you, it might belong here. If EF
can express it, it belongs in a migration.

## Naming

Prefix with a two-digit number so the order is explicit rather than alphabetical luck:
`00-extensions.sql`, `10-roles.sql`. Both `.sql` and `.sh` are supported.

The scripts run as the superuser (`POSTGRES_USER`) against `POSTGRES_DB`. The directory is mounted
read-only, so a script cannot rewrite itself or its siblings.
