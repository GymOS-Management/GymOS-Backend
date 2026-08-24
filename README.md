# GymOS-Backend

**The** GymOS backend — the entire server side as one modular monolith
(`.NET 10 / SQL Server`), per the frozen architecture in
[GymOS-Specification](../GymOS-Specification) (repository map: **ADR-52**).
Serves every client: the Super Admin console ([GymOS-Platform](../GymOS-Platform)),
the gym web app ([GymOS-Admin](../GymOS-Admin)), the member app, and gateways.

## Layout

```
GymOS.Backend.slnx
Directory.Build.props        # quality gates: nullable, warnings-as-errors, analyzers
Directory.Packages.props     # Central Package Management — the dependency audit point
docs/Engineering/            # DEPENDENCY_POLICY.md — changes with its enforcing test
src/
  BuildingBlocks/            # SharedKernel + Application/Infrastructure building blocks
  Platform/                  # platform-scope vertical: gyms/tenants, subscriptions,
                             #   platform users, feature flags, settings (M1+)
  Modules/                   # feature modules land here per milestone (Members,
                             #   Scheduling, POS, Gateway, AI, …)
  Host/GymOS.WebApi/         # composition root — the single deployable
tests/
  GymOS.ArchitectureTests/   # dependency + package law; runs on every build
database/
  GymOS.Database/            # the schema, declaratively — generated from the EF
                             #   migrations, compared against live databases,
                             #   never published. See database/README.md
scripts/                     # schema-compare.ps1, reset-local-db.ps1, populate-all.ps1
```

Note: the `GymOS.Platform.*` project names refer to the platform-scope module area
inside this backend (ARCHITECTURE-EXTENSIONS.md §9) — unrelated to the GymOS-Platform
frontend repository.

## Local development

The whole stack, from a clean clone, with no setup step:

```
docker compose up -d       # SQL Server 2022 + API + Mailpit, migrated and seeded, on http://localhost:5080
```

Sign in as `admin@gymos.local` / `gymos-dev-admin-password` — deliberately public development
credentials, like the signing key in `appsettings.Development.json`. Full reference, including the
production workflow and every security decision: [`docs/Infrastructure/DOCKER.md`](docs/Infrastructure/DOCKER.md).

**In Visual Studio, `F5` on the `docker-compose` project does the same thing** and is the shortest
path to a working stack: it builds the real image, starts all three containers, and opens the API
reference. Nothing else needs starting by hand — email included, so inviting an administrator
actually sends a message (readable at <http://localhost:8025>). The only piece outside this repo is
the console itself: run GymOS-Platform separately if you want to follow an activation link.

### Running the API on the host

The usual working setup: the API on the host, only the database in a container.

**1. Restore the pinned tools** (fresh clone only — `dotnet ef` is a local tool):

```
dotnet tool restore
```

**2. Start SQL Server:**

```
docker compose up -d mssql db-init
```

**3. Give yourself an account to sign in with.** Skip this and the seeder warns and creates
nothing — GymOS never ships a default credential:

```
dotnet user-secrets set "Seed:SuperAdmin:Email" "you@example.com"  --project src/Host/GymOS.WebApi
dotnet user-secrets set "Seed:SuperAdmin:Password" "<12+ chars>"   --project src/Host/GymOS.WebApi
```

**4. Run it:**

```
dotnet run --project src/Host/GymOS.WebApi       # http://localhost:5080, opens /scalar/v1
```

In Development, `Database:MigrateOnStartup` is `true`, so the API migrates and seeds itself on
boot — there is no separate `dotnet ef database update` step. That cuts both ways: **the first run
after pulling applies any new migration to whichever database it is pointed at.**

**Check which database that is before the first run of the day.** Configuration precedence is
environment variables → user secrets → `appsettings.Development.json`, so a
`ConnectionStrings:Database` left in your user secrets (a shared team database, say) silently wins
over the local container:

```
dotnet user-secrets list --project src/Host/GymOS.WebApi
```

To point one run at the local container without changing anything, set the environment variable —
it outranks the secret:

```powershell
$env:ConnectionStrings__Database='Server=localhost,1433;Database=gymos;User Id=gymos_app;Password=Gymos_dev_app_password1;Encrypt=True;TrustServerCertificate=True;Current Language=us_english'
```

(Those credentials match `docker-compose.override.yml` and are deliberately public, like the
signing key.)

Starting the API opens the **Scalar API reference** at `http://localhost:5080/scalar/v1` in
your browser; the document behind it is `http://localhost:5080/openapi/v1.json`. Sign in
against `POST /api/v1/auth/login` and paste the access token into Scalar's authorize control
to call the secured endpoints.

Both are **published in Development only** — the document maps every endpoint, parameter and
schema, so elsewhere it is off unless an operator sets `OpenApi:Enabled` (or
`OpenApi__Enabled=true`). Set `OpenApi:LaunchBrowser` to `false` if you would rather no tab
opened; it never opens from a container.

The development JWT signing key lives in `appsettings.Development.json` and is public by
design — every other environment must supply `Security:Jwt:SigningKey` (startup fails fast
without it).

## Email — inviting an administrator (ADR-53)

`POST /users` creates an administrator in `PendingActivation` with no password, and they activate
through a link they receive by email. So **without email configured, an invited administrator
cannot sign in.**

**`Email:Enabled` defaults to `false`**, so the API needs no SMTP server to run and the test suite
needs none either. Disabled, an invitation still succeeds and logs
`Email is disabled (Email:Enabled=false); '<subject>' for <recipient> was not sent.`

**That log line carries no link, deliberately** — the body holds a one-time credential, and a log
entry containing it would be a second copy of it. So **you cannot complete an activation with email
disabled**: there is no way to obtain the token, which is the property the flow depends on. To follow
the flow at all, point it at a local sink.

Use the **Mailpit** service in the compose file — SMTP with a web inbox, so nothing leaves the
machine. It is part of the development baseline, so `docker compose up -d` starts it alongside
SQL Server and the API:

```
docker compose up -d mssql db-init mailpit   # SMTP on :1025, inbox on http://localhost:8025
```

```
dotnet user-secrets set "Email:Enabled" "true"                          --project src/Host/GymOS.WebApi
dotnet user-secrets set "Email:Smtp:Host" "localhost"                   --project src/Host/GymOS.WebApi
dotnet user-secrets set "Email:Smtp:Port" "1025"                        --project src/Host/GymOS.WebApi
dotnet user-secrets set "Email:From:Address" "platform@gymos.local"     --project src/Host/GymOS.WebApi
dotnet user-secrets set "Email:From:DisplayName" "GymOS Platform"       --project src/Host/GymOS.WebApi
dotnet user-secrets set "Email:ConsoleBaseUrl" "http://localhost:3000"  --project src/Host/GymOS.WebApi
```

**`Email:ConsoleBaseUrl` must be wherever the console actually runs** (GymOS-Platform, `:3000` by
default). Every activation link is built from it, so a wrong value is already in somebody's inbox
before anyone notices — which is why, with email enabled, a missing or non-absolute value is a
**startup** failure rather than a first-send surprise. `Email:Smtp:Host` and `Email:From:Address`
are checked the same way.

**Running the API inside the compose stack needs none of the above.** The development overlay already
turns email on and points the `api` service at the Mailpit sidecar, so `docker compose up -d` — or
**F5 in Visual Studio** — gives you a stack where inviting an administrator sends a real message you
can read. Note the SMTP host there is `mailpit`, not `localhost`: containers reach each other by
service name, not through your host's published ports.

Two overlay values you may want to override in `.env`: `GYMOS_CONSOLE_URL` if the console is not on
`http://localhost:3000` (every activation link is built from it, and it is followed by a browser on
your host, so it must be host-reachable), and `GYMOS_EMAIL_FROM` for the sender address.

Other keys, with their defaults: `Email:Smtp:UseStartTls` (`false`, for a local sink that speaks
plain SMTP), `Email:Smtp:Username` / `Password` (blank locally, required by any real provider),
`Email:InvitationLifetimeHours` (`72` — the recipient is not expecting it and may be away) and
`Email:PasswordResetLifetimeHours` (`1` — the account already has a credential, so a live reset
link is a second way in).

### What to expect

- Invite from the console, or `POST /api/v1/users`. The response carries
  **`X-Invitation-Email: sent | failed`** beside the usual body: the account is created either way,
  because an unreachable mail server is no reason to undo it.

  ⚠ **`sent` also means "email is disabled"** — the header reports that nothing *failed*, and with
  `Email:Enabled=false` nothing was attempted. So a `sent` you did not expect is the first thing to
  check when no message arrives. The header wants a third value; recorded here rather than left to
  be discovered.
- On `failed`, **`POST /api/v1/users/{id}/resend-invitation`** (or *Re-send invitation* in the
  console) is the recovery path. Issuing a new link invalidates the previous one, so at most one is
  ever live.
- **`POST /api/v1/users/{id}/reset-password`** emails an established administrator a reset link. It
  sends a link and never sets a password — §3.6: an administrator who could set another
  administrator's password would hold a credential that is no longer one.
- The link lands on the console's `/set-password`, works **once**, and is spent even if the password
  it carries is rejected by policy — otherwise a refused attempt would leave a replayable link
  behind. Every failure (unknown, expired, already used, account suspended) answers identically, on
  purpose: a distinct "expired" would confirm to an anonymous caller that the token had once been
  real.

## Troubleshooting

- **A build suddenly cannot copy DLLs (`MSB3027`/`MSB3021`).** A previous `dotnet run` is still
  holding the output. Stop it before rebuilding — this has cost time three times now.
- **`Login failed for user 'gymos_app'`.** A natively installed SQL Server or LocalDB is winning
  port 1433 over the container. Set `MSSQL_PORT=1434` in `.env`, or stop the local service. The
  containerised API is unaffected — it reaches the database over the compose network.
- **The API starts but talks to the wrong database.** See the precedence note above and run
  `dotnet user-secrets list`.
- **Invitations report `failed`.** Either no SMTP server is listening on `Email:Smtp:Host`/`Port`, or
  Mailpit is not running. Confirm the inbox answers on <http://localhost:8025>, then re-send.
- **An invited administrator cannot sign in and no email arrived.** Check `Email:Enabled` — with it
  off, nothing is sent and the link cannot be recovered from the log (by design). Enable it, then
  re-send the invitation.
- **`dotnet ef migrations remove` needs a reachable database** — it connects to check whether the
  migration was applied. With nothing up, restore `AppDbContextModelSnapshot.cs` from git and delete
  the two migration files by hand, or the next `add` produces an empty migration.

## API contract

`contracts/openapi-v1.json` is the committed shape of v1 and is diffed by the test suite
(AD-37/R13). Any endpoint change fails that test until the snapshot is regenerated — which
is the point: the diff goes through review.

```
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests
git diff contracts/openapi-v1.json
```

Additive changes (new endpoint, new optional field) are compatible within a version.
Removing, renaming, retyping, or changing the meaning of a field needs a new version.

`dotnet ef` is a pinned local tool (`dotnet-tools.json`) — run `dotnet tool restore` on a
fresh clone. New migrations:
`dotnet ef migrations add <Name> --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi --output-dir Persistence/Migrations`

**Adding a migration means regenerating `database/GymOS.Database` in the same commit.** That
project is the declarative record of what the migrations produce — it is what lets you ask whether
a live database actually matches, which the migrations themselves cannot answer. It is derived from
`dotnet ef migrations script` and is a lie the moment it lags. It never deploys: EF migrations are
the only thing that changes a database, in every environment.

```
dotnet build database/GymOS.Database/GymOS.Database.sqlproj -c Release   # -> a DACPAC
./scripts/schema-compare.ps1 -ConnectionString "<target>"                # read-only drift report
```

Full reference, including the three objects that are deliberately not comparable:
[`database/README.md`](database/README.md).

## Build & test

```
dotnet build GymOS.Backend.slnx
dotnet test  GymOS.Backend.slnx
```

## Rules

- Architecture is frozen — see GymOS-Specification; deviations need an ADR there first.
- Dependencies are allowlisted — see `docs/Engineering/DEPENDENCY_POLICY.md`; the policy
  document and `ForbiddenPackagesTests` change together, in the same commit.
- New projects must be declared in the `ProjectReferenceTests` rule table or the build fails.
