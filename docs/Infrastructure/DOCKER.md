# Docker infrastructure

**Status:** production-grade for a single host. Two services run today; the compose files are shaped
so the eleven planned ones arrive without redesign.

```bash
docker compose up -d          # that is the whole setup step
```

No `.env`, no database install, no migration command. The stack comes up with PostgreSQL 17, the API
migrated and seeded, and a working sign-in.

---

## 1. Architecture

```
                      host
  ┌──────────────────────────────────────────────────────────────────────┐
  │                                                                      │
  │   dev:  localhost:5080          prod:  127.0.0.1:8080                │
  │                │                              │                      │
  │                ▼                              ▼                      │
  │   ╔════════════════════════════════════════════════════╗             │
  │   ║  network: gymos-edge                    (bridge)   ║             │
  │   ║                                                    ║             │
  │   ║      ┌───────────────────────────────┐             ║             │
  │   ║      │  gymos-api                    │             ║             │
  │   ║      │  aspnet:10.0 · uid 1654       │             ║             │
  │   ║      │  read-only rootfs · caps: none│             ║             │
  │   ║      │  :8080  /health/{live,ready}  │             ║             │
  │   ║      └───────────────┬───────────────┘             ║             │
  │   ╚══════════════════════│═════════════════════════════╝             │
  │                          │  the only path between the two            │
  │   ╔══════════════════════│═════════════════════════════╗             │
  │   ║  network: gymos-data │      (internal in prod:     ║             │
  │   ║                      ▼       no route to internet)  ║             │
  │   ║      ┌───────────────────────────────┐             ║             │
  │   ║      │  gymos-postgres               │             ║             │
  │   ║      │  postgres:17-alpine           │             ║             │
  │   ║      │  UTF8 · lc=C · UTC            │             ║             │
  │   ║      │  :5432  pg_isready            │             ║             │
  │   ║      └───────────────┬───────────────┘             ║             │
  │   ║                      │                              ║            │
  │   ║   profiles (off by default):                        ║            │
  │   ║   redis · seq · minio · pgadmin · mailpit           ║            │
  │   ╚══════════════════════│═════════════════════════════╝             │
  │                          ▼                                           │
  │            volume: gymos-postgres-data                               │
  └──────────────────────────────────────────────────────────────────────┘
```

Files, and which environment each applies to:

| File | Applied | Contains |
|---|---|---|
| `docker-compose.yml` | always | Service topology, health checks, volumes, networks, hardening. No credentials, no published ports. |
| `docker-compose.override.yml` | **automatically** | Development: public credentials, published ports, relaxed Argon2 cost, email on via the Mailpit sidecar, pgAdmin behind a profile. |
| `docker-compose.prod.yml` | only when named | Production: required secrets, no database port, loopback binding, internal data network, resource limits. |
| `src/Host/GymOS.WebApi/Dockerfile` | build | Three stages: restore → publish → runtime. |
| `.env.example` | template | Every variable, with defaults. Copy to `.env` only to change something. |

The asymmetry is deliberate: the *development* overlay is the automatic one, so forgetting a flag
gives you the hardened stack on a laptop, never the laptop stack in production.

---

## 2. Container responsibilities

### `gymos-postgres` — PostgreSQL 17

The only stateful service. Initialised once with `--encoding=UTF8 --lc-collate=C --lc-ctype=C` and
`TZ=UTC`; none of those can change afterwards without dumping and restoring, and `lc-collate=C`
keeps sort order identical on every machine — an index built under one collation is not valid under
another.

Health is `pg_isready -U … -d …` with the user and database named explicitly. Bare `pg_isready`
reports healthy during the window when the entrypoint has a temporary server up for the init
scripts and the real database does not yet exist, which would let the API start too early.

### `gymos-api` — GymOS Web API

Stateless. Migrates and seeds at start-up in development; in production it expects a migrated
database (§6). Two endpoints matter to infrastructure:

| Endpoint | Checks | Used for |
|---|---|---|
| `/health/live` | nothing, by design | Liveness. "The process is up." A dependency outage must not make the orchestrator kill a healthy process. |
| `/health/ready` | database connectivity | Readiness, and what `depends_on: service_healthy` and the container `HEALTHCHECK` use. |

Those map one-to-one onto Kubernetes `livenessProbe` and `readinessProbe`, which is why the split
exists before there is a Kubernetes.

---

## 3. Networks

**Why not the default bridge.** It has no embedded DNS, so containers can only find each other by IP
or the deprecated `--link` — `Host=postgres` would not resolve. It is also shared by every container
on the host that did not choose otherwise, so an unrelated `docker run` is one command away from
having a route to this database.

**Why two.** `gymos-data` is marked `internal: true` in production, which removes its route to the
outside world entirely — verified: a `wget` from inside the postgres container times out. The API
sits on both networks and is the only path between them. That turns "the database should not be
exposed" from a convention into a property of the network.

| Network | Members | Notes |
|---|---|---|
| `gymos-edge` | api | The only network with a route in from the host. |
| `gymos-data` | api, postgres, and every optional service | `internal: true` in production. |

---

## 4. Volumes

Named volumes throughout, never host bind mounts: Docker manages ownership (a bind mount from a
Windows host has no useful notion of the container's uid), `docker compose down` cannot delete them
by accident, and they are the unit a backup operates on.

| Volume | Mounted by | Purpose |
|---|---|---|
| `gymos-postgres-data` | postgres | The database. The one volume whose loss matters. |
| `gymos-api-dataprotection` | api | ASP.NET DataProtection keys. Nothing depends on them yet (the API is JWT-based and stateless); it exists so that when something does, restarts do not invalidate it. |
| `gymos-uploads` | api | Reserved for the validated-upload pipeline (AD-44). |
| `gymos-logs` | **nothing** | Declared, deliberately unmounted — see below. |
| `gymos-redis-data`, `gymos-seq-data`, `gymos-minio-data`, `gymos-prometheus-data`, `gymos-grafana-data`, `gymos-pgadmin-data` | optional services | Declared so enabling a profile needs no edit. |

**Why `logs` is not mounted.** The API logs to stdout, which is what makes `docker compose logs`,
the json-file driver, and any future collector work without the application knowing they exist. A
log *file* inside a container is the thing centralised logging replaces: invisible from outside,
surviving nothing, and competing with the database for disk. The volume exists so that a service
which genuinely produces files — a nightly export, a diagnostic dump — has somewhere named to put
them.

---

## 5. Environment variables

Names mirror the application's own configuration keys, because ASP.NET maps `A__B__C` to `A:B:C`.
There is one naming scheme, not two. Full list with defaults: [`.env.example`](../../.env.example).

### The one that will catch you

**An empty environment variable is not an absent one, and it outranks `appsettings.json`.** Setting
`Security__Jwt__SigningKey: ""` in the base compose file overrode the deliberately public
development key in `appsettings.Development.json`, and the API refused to start with *"SigningKey
field is required"* — in the one environment designed to need no secrets. That is why the base file
declares no secrets at all and the overlays declare them instead. If you add a setting that has a
file-based default, do not give it an empty compose default.

### Required where

| Variable | Development | Production |
|---|---|---|
| `POSTGRES_PASSWORD` | public default (`gymos_dev_password`) | **required** — `${VAR:?}` aborts the command |
| `GYMOS_JWT_SIGNING_KEY` | unset; file fallback applies | **required**, ≥32 bytes, validated at start-up |
| `API_IMAGE` | built from source | **required** — production runs the artefact CI tested |
| `GYMOS_SEED_ADMIN_*` | `admin@gymos.local` / `gymos-dev-admin-password` | set for a new environment's first deploy, then unset |
| `GYMOS_MIGRATE_ON_STARTUP` | `true` | `false` — see §6 |
| `GYMOS_FORWARDED_HEADERS_ENABLED` | `false` | `true` behind a proxy, or the rate limiter sees one bucket for everyone |

Generate a signing key with `openssl rand -base64 48`.

---

## 6. Workflows

### Development

```bash
docker compose up -d                  # PostgreSQL + API, migrated and seeded
docker compose logs -f api            # follow
docker compose down                   # stop, keep data
docker compose down -v                # stop, destroy data
```

Sign in at `http://localhost:5080` with `admin@gymos.local` / `gymos-dev-admin-password`. The
Super Admin console (GymOS-Platform) proxies to this API — see that repository's README.

Optional services:

```bash
docker compose --profile tools up -d          # pgAdmin :5050  (Mailpit already runs by default)
docker compose --profile redis up -d          # Redis :6379
docker compose --profile observability up -d  # Seq UI :8341, ingestion :5341
docker compose --profile storage up -d        # MinIO API :9000, console :9001
```

Working on the API itself is usually faster outside the container — `dotnet run` with only
PostgreSQL in Docker, which is what the port on 5432 is published for.

### Production

```bash
# 1. Build and push in CI, tagged with the release.
docker build -f src/Host/GymOS.WebApi/Dockerfile -t ghcr.io/gymos-management/gymos-api:0.7.0 .
docker push ghcr.io/gymos-management/gymos-api:0.7.0

# 2. Apply migrations as their own step, BEFORE deploying.
dotnet ef migrations script --idempotent \
    --project src/Platform/GymOS.Platform.Infrastructure \
    --startup-project src/Host/GymOS.WebApi \
    --output migrate.sql
psql "$PRODUCTION_CONNECTION_STRING" -v ON_ERROR_STOP=1 -f migrate.sql

# 3. Deploy.
export POSTGRES_PASSWORD=…  GYMOS_JWT_SIGNING_KEY=…  API_IMAGE=ghcr.io/…/gymos-api:0.7.0
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d
```

**Why migrations are a separate step.** `Database__MigrateOnStartup` is off in production because
applying schema changes as a side effect of a container start means two replicas can migrate the
same database concurrently, and a failed migration takes out the very process that was supposed to
report it. The script is *idempotent*, so re-running it is safe, and it can be reviewed before it
touches anything. The runtime image has no SDK by design, which is why the script is generated
where the SDK lives.

### Continuous integration

`.github/workflows/ci.yml` has a `docker` job, running in parallel with the tests, that does on a
clean checkout exactly what a new contributor does: build the image, `docker compose up -d`, and
check that the result is usable. It then asserts the hardening — non-root uid, read-only root
filesystem, `cap_drop: ALL`, `no-new-privileges`, postgres on the data network only — because a
security setting nobody verifies is a security setting that can be removed by accident. It then
starts every optional profile and requires all seven containers to report healthy, and proves the
production overlay *refuses* to render when its secrets are unset.

The full production boot is not in CI: it needs the migration step (§6), which needs the SDK the
runtime image deliberately lacks. CI validates the production overlay statically; the boot itself is
verified by hand at release time.

Production expects a reverse proxy in front for TLS (Azure Front Door, Caddy, an ingress
controller). Set `GYMOS_FORWARDED_HEADERS_ENABLED=true` when one is there, and not before — with it
on and no proxy, a client can spoof its own address.

---

## 7. Security decisions

Every one of these is verifiable with `docker inspect`; the commands are in §9.

| Decision | Rationale |
|---|---|
| **API runs as uid 1654 (`app`), not root** | The .NET images predefine the user; `USER $APP_UID` is one line. `USER` is set *after* `COPY`, so the application's own binaries stay root-owned and it cannot rewrite them. |
| **API root filesystem is read-only** | The application writes nothing: it logs to stdout, caches nothing locally, and keeps DataProtection keys on a volume. `read_only: true` turns "should not write" into "cannot write". Kept on in development too, so a new write requirement fails on a laptop rather than in production. |
| **`/tmp` is a 64 MB tmpfs** | The one path the framework may genuinely need, sized so a runaway temp file cannot consume host memory. |
| **`cap_drop: ALL` everywhere** | The default Docker capability set includes things (NET_RAW, MKNOD) that no GymOS service uses. Start from nothing. |
| **Postgres adds back CHOWN, DAC_OVERRIDE, FOWNER, SETGID, SETUID** | Its entrypoint chowns the data directory and drops from root to `postgres`; without these it exits on first boot. The API adds back nothing. |
| **`no-new-privileges:true`** | A process inside the container can never gain more rights than it started with, even via a setuid binary. |
| **Postgres is not read-only** | It writes WAL, sockets, and transient files outside the data volume. tmpfs-ing them all to claim the flag would be ceremony, not security. |
| **No database port in production** | `ports: []` overrides the development publication. Administer with `docker compose exec postgres psql`, or an SSH tunnel. |
| **API published on 127.0.0.1 in production** | Only a reverse proxy on the same host should reach it directly. `API_BIND=0.0.0.0` is a deliberate act. |
| **`gymos-data` is `internal` in production** | The database has no route to the internet, so a compromised dependency inside it has nowhere to send what it finds. |
| **Log rotation on every container** | `max-size: 10m, max-file: 3`. Without a cap, one log-looping container fills the host disk and takes the database with it. |
| **Memory limits in production** | A runaway query cannot starve the API on the same host. Sized for a small deployment, not derived from measurement — raise them once you have numbers. |
| **Secrets never in an image or a committed file** | `.env` is gitignored and `.dockerignore`d; `.env.example` holds placeholders and development defaults only. Development credentials are public *on purpose*, exactly like `appsettings.Development.json`. |
| **No `latest` for the API in production** | `API_IMAGE` is required and should carry a version, so what is deployed is identifiable and re-deployable. |

Optional services use `latest` because they are development conveniences, not deployed components;
pin them if any becomes production infrastructure.

### Verified posture, every service

Measured with every profile enabled, not asserted from the YAML. `docker compose exec … id -u`
reports the *exec* user; for postgres that is the entrypoint's root, while the server processes
themselves run as `postgres` (`ps -o user,args`).

| Service | Runs as | `cap_drop` | `cap_add` | Why anything is added back |
|---|---|---|---|---|
| api | 1654 `app` | ALL | — | Nothing. Read-only rootfs as well. |
| postgres | entrypoint root → server `postgres` | ALL | CHOWN, DAC_OVERRIDE, FOWNER, SETGID, SETUID | Chowns the data directory and drops privilege; exits on first boot without them. |
| redis | 999 `redis` | ALL | — | `user: redis` starts unprivileged, so the setpriv drop never happens. |
| seq | 999 `seq` | ALL | NET_BIND_SERVICE | Its binary carries that file capability; exec fails with EPERM if it is outside the bounding set. |
| minio | root | ALL | — | Image limitation, documented in the service block: root-owned `/data`. Development stand-in only. |
| mailpit | 1000 | ALL | — | Nothing to write, nothing privileged to do. |
| pgadmin | 5050 `pgadmin` | ALL | — | Listens on 8080 instead of 80, so no capability is needed to bind. |

Each of these was found by booting the service, not by reading its Dockerfile. Redis, Seq, and
pgAdmin all crash-looped on first contact with `cap_drop: ALL`, and the messages were unhelpful
(`setpriv: setresuid failed`, `/seqsvr/Seq: Operation not permitted`, a silent port fallback) — so if
you enable a new profile and it restarts forever, capabilities are the first thing to suspect.

---

## 8. Logging

The API writes structured events to stdout, and `Serilog__ConsoleFormat` decides the shape:

| Value | Behaviour |
|---|---|
| unset | Environment-derived: readable text in Development, JSON elsewhere. |
| `text` | Human-readable. What the development overlay sets, because `docker compose logs -f api` is read by a person. |
| `json` | Serilog `CompactJsonFormatter`. What production sets, because those lines are read by a collector. |

The override exists for container parity: the same image runs everywhere, and without a way to
choose, either the operator debugging a container or the log pipeline is always looking at the wrong
format. Set `GYMOS_LOG_FORMAT=json` locally to see exactly what production emits.

Centralised logging needs no application change: `docker compose --profile observability up -d`
starts Seq on `http://localhost:8341` (sign in as `admin` / `gymos-dev-seq`, ingestion on 5341), and
`OTEL_EXPORTER_OTLP_ENDPOINT` activates the OpenTelemetry export that AD-38 already wired up. The log driver is `json-file`, so a collector reading the host's log files gets structured
events without a parsing step.

---

## 9. Common commands

```bash
# State
docker compose ps
docker compose logs -f api
docker inspect --format '{{.State.Health.Status}}' gymos-api

# Verify the security posture (should print: false-for-root, ALL, no-new-privileges, true)
docker compose exec api id
docker inspect --format '{{.HostConfig.CapDrop}}'      gymos-api
docker inspect --format '{{.HostConfig.SecurityOpt}}'  gymos-api
docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' gymos-api
docker network inspect gymos-data --format '{{.Internal}}'

# Database
docker compose exec postgres psql -U gymos -d gymos
docker compose exec postgres psql -U gymos -d gymos -c '\dt platform.*'

# Rebuild after a code change (layer cache makes this fast)
docker compose up -d --build api

# Reset everything, including data
docker compose down -v
```

---

## 10. Troubleshooting

**`password authentication failed for user "gymos"`**
`POSTGRES_PASSWORD` is applied **only at initdb**, on an empty data directory. Changing it later has
no effect on an existing volume — this is the single most common confusion here, and it is how a
production smoke test failed during this work. Either change it inside the database
(`ALTER USER gymos WITH PASSWORD '…'`) or destroy the volume (`docker compose down -v`, which
destroys the data with it).

**`relation "platform.users" does not exist`**
The database has no schema. In development, `Database__MigrateOnStartup` should be `true`; in
production, run the migration script (§6) *before* starting the API. Both seeders now skip with a
clear warning when migrations are pending rather than throwing this.

**API is `unhealthy`, logs mention `SigningKey field is required`**
Something set `Security__Jwt__SigningKey` to an empty value, which overrides the file-based
development key. See §5.

**My local database is suddenly empty**
The postgres volume was renamed `gymos-pgdata` → `gymos-postgres-data` when this infrastructure
landed, so a pre-existing local database is still on disk but no longer mounted. Recover it with
`docker run --rm -v gymos-pgdata:/from -v gymos-postgres-data:/to alpine sh -c 'cp -a /from/. /to/'`
(with the stack stopped), or just let it re-migrate and re-seed — local data is disposable.

**`An error occurred while reading the key ring` / `Access to the path … is denied`**
A writable path is root-owned while the process runs as uid 1654. A named volume inherits the
ownership of the image directory it covers, and a directory absent from the image is created
root-owned — so the Dockerfile creates `/home/app/.aspnet/DataProtection-Keys` and
`/var/lib/gymos/uploads` with the right ownership before dropping to `USER`. If you add a volume,
add its directory there too. Note the failure mode: the API logs an error, falls back to in-memory
DataProtection keys, and still reports healthy — nothing breaks until a restart invalidates
something the keys protected.

**Port already allocated**
A native PostgreSQL usually owns 5432 and wins. Set `POSTGRES_PORT=5433` in `.env`; the API is
unaffected because it reaches the database over the compose network, not the published port.

**The API cannot reach the database**
Check both are on `gymos-data` (`docker inspect --format '{{range $k,$v :=
.NetworkSettings.Networks}}{{$k}} {{end}}' gymos-api`). The host must be `postgres`, the service
name — `localhost` inside the API container is the API container.

**An init script did not run**
They execute only on an empty data directory, in filename order. See
`docker/postgres/initdb/README.md`.

**Build fails on an analyzer rule that passes locally**
The image build needs every root-level build file. `.editorconfig` carries analyzer severities —
including the one that permits the SharedKernel's `Error` type — so omitting it makes the image
compile under different rules than CI. It is copied in the restore stage for exactly this reason.

---

## 11. Backups

Only `gymos-postgres-data` holds anything whose loss matters. Uploads will join it when AD-44 lands.

```bash
# Logical backup — portable across versions, restorable table by table.
docker compose exec -T postgres pg_dump -U gymos -d gymos --format=custom \
    > "backup-$(date +%Y%m%d-%H%M%S).dump"

# Restore into an empty database.
docker compose exec -T postgres pg_restore -U gymos -d gymos --clean --if-exists < backup.dump
```

`pg_dump`, not a volume copy: a filesystem snapshot of a running PostgreSQL is only consistent if
the volume snapshot is atomic, and a custom-format dump can be restored selectively and into a
different minor version.

**A backup nobody has restored is a hypothesis.** Restore into a throwaway container periodically:

```bash
docker run --rm -d --name pg-restore-test -e POSTGRES_PASSWORD=test -e POSTGRES_DB=gymos \
    -e POSTGRES_USER=gymos postgres:17-alpine
docker exec -i pg-restore-test pg_restore -U gymos -d gymos --clean --if-exists < backup.dump
docker exec pg-restore-test psql -U gymos -d gymos -c 'SELECT count(*) FROM platform.gyms;'
docker rm -f pg-restore-test
```

For anything with real users, prefer a managed database (Azure Database for PostgreSQL Flexible
Server, AD-11): point-in-time restore, automated backups, and patching stop being someone's weekend.
Deleting the `postgres` service and repointing `ConnectionStrings__Database` is the whole migration.

---

## 12. Updates

| What | How | Notes |
|---|---|---|
| **API** | Build a new tag in CI, apply migrations (§6), `up -d`. | Compose recreates the container. Single-host has a brief gap; a proxy plus two replicas removes it. |
| **PostgreSQL patch** (17.x → 17.y) | `docker compose pull postgres && docker compose up -d postgres` | Same major: the data directory is compatible. Back up first anyway. |
| **PostgreSQL major** (17 → 18) | `pg_dump`, start the new major on an empty volume, `pg_restore`. | The data directory is **not** compatible across majors. Never a `pull`. |
| **Base images** | Rebuild. `mcr.microsoft.com/dotnet/aspnet:10.0` is a moving tag that picks up runtime security patches. | Rebuild on a schedule, not only when the code changes — an image untouched for months is an unpatched image. |
| **Optional services** | `docker compose --profile … pull` | They use `latest`; pin any that becomes production infrastructure. |

---

## 13. Adding a service

The compose files are structured so this needs no change to what exists:

1. Add the service to `docker-compose.yml` with `profiles: ['…']` while it is optional.
2. Give it `<<: *default-hardening` and `logging: *default-logging` — hardening is inherited, not
   remembered. **YAML anchors do not cross files**: a service defined only in
   `docker-compose.override.yml` cannot see the base file's anchors and silently gets Docker's
   default capability set instead. That file defines its own `x-hardening` for this reason, and it
   is how pgAdmin and Mailpit were initially left unhardened.
3. Put it on `gymos-data` unless it must be reachable from outside; only then `gymos-edge`.
4. Give it a health check, and have dependents wait on `condition: service_healthy`.
5. Declare its volume in the `volumes:` block with a `gymos-` prefixed name.
6. Add its variables to `.env.example` with development defaults, and publish its host port from
   the override file — publishing is an environment decision, not part of the topology.
7. **Start it before believing it.** `docker compose config` proves the YAML parses and nothing
   more; three of the four optional services failed on first boot under `cap_drop: ALL`.

`redis`, `seq`, and `minio` are already defined this way — profiles rather than commented-out YAML,
because a commented block rots quietly and nobody finds the indentation error until the day they
need it. All four profiles have been booted and are healthy (§7).

Planned but not yet defined, each needing only a service block and a profile: `gateway` (AD-47),
`worker` (the M3.4 renewal sweep, M5 stats, audit retention — the same image as `api` with a
different entrypoint), `ai-worker` (AD-31), `prometheus`, and `grafana`.

---

## 14. Kubernetes and Azure

Nothing here needs redesigning to move:

- **One image**, configured entirely by environment variables → a ConfigMap and a Secret.
- **`/health/live` and `/health/ready`** → `livenessProbe` and `readinessProbe`, already separated
  along the axis Kubernetes needs.
- **No local state in the API** → any replica count works; only PostgreSQL is stateful, and it
  should be a managed service by then (AD-11).
- **Structured JSON to stdout** → whatever the cluster collects with.
- **Non-root, read-only, capabilities dropped** → these *are* a Pod `securityContext`, so a
  restricted Pod Security Standard is already satisfied.
- **Migrations as a separate idempotent step** → an init container or a Job, which is what a
  cluster wants anyway.
