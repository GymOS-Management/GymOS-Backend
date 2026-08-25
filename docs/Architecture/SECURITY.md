# Security model

What protects what, and where each control is enforced. Detail that already has a home is linked
rather than repeated.

---

## 1. Request authentication and authorization

**Bearer JWT** (AD-4), 15-minute access tokens, refresh tokens in an httpOnly cookie with rotation.
Signing key: 32 bytes minimum, and **startup fails without one** in every environment except
Development, where a deliberately public key lives in `appsettings.Development.json` (identical for
every developer, loaded only under `Development`, reaching nothing but a container on localhost).

**Passwords: Argon2id**, cost parameters in `appsettings.json` so they are reviewable and tunable
without a code change.

**Authorization is permission-based, never role-based at the call site.** Endpoints declare a
permission (`RequireAuthorization(PlatformPermissions.AnalyticsRead)`); roles are a grouping of
permissions in data. Two tests hold the line, and both run on every build:

- `EndpointConventionTests` — every `/api/**` endpoint declares a permission or appears in an
  explicit exemption list. A new endpoint that forgets is a failing build, not a security review
  finding. The exemptions are three: login (issues the token), refresh (authorized by the cookie),
  and set-password (ADR-53 — the recipient cannot sign in yet, so the one-time token *is* the
  credential).
- `OpenApiExposureTests` and the document transformer — the published contract declares which
  operations require a token, so a generated client cannot silently omit auth.

**Rate limiting** partitions on client IP, ahead of everything in the pipeline, because the audit
trail records that address and the limiter must see the real one — which is why forwarded-headers
handling comes first and is configuration-driven.

---

## 2. Tenant isolation (R14)

`tenant_id` plus SQL Server row-level security, with the session's tenant set at the connection
level (`sp_set_session_context`) by an interceptor rather than by each query remembering to filter.

Two properties make it a control rather than a convention:

- **`TenantIsolationValidator` runs at startup and is fatal.** A tenant-scoped table without a
  *forced* policy means isolation is off, and the only safe response is to not serve traffic.
- **The application principal cannot switch the policy off.** That needs `ALTER ANY SECURITY POLICY`,
  which `gymos_app` does not hold — and the validator refuses to boot if it turns out to.

Reading its own policies requires `VIEW DEFINITION` on the schema. Without that grant the validator
reads row-level security as *absent* and fails closed, which is the correct direction and a confusing
morning. It is granted; the runbook says so.

Full mechanism, the known gaps, and what is designed but not built:
[`docs/BusinessRules/TenantIsolation.md`](../BusinessRules/TenantIsolation.md).

---

## 3. Database principals — the split that matters most

Three principals, and the important one is what the API *cannot* do.

| Principal | Holds | Used by |
|---|---|---|
| `gymos_admin` / server admin | everything | humans and maintenance only, never an application |
| `gymos_migrator` | `db_ddladmin`, `db_datareader`, `db_datawriter`, `ALTER ANY SECURITY POLICY`; owns the `platform` and `outbox` schemas | the one-shot migration step only |
| `gymos_app` | `db_datareader`, `db_datawriter`, `EXECUTE` on `platform`, `VIEW DEFINITION` | every API instance, every request |

**`db_ddladmin`, not `db_owner`.** It carries every `CREATE`/`ALTER`/`DROP` a migration issues, plus
the `ALTER ANY DATASPACE` that audit partition maintenance needs — and it does **not** carry the
ability to add members to roles or grant itself more. That is the entire reason for not using
`db_owner`, and there is no documented exception to it in any environment.

**The API holds no DDL rights in any environment, including Development.** `Database:MigrateOnStartup`
is `false` in every compose file and every appsettings file; migrations are a separate step
(`Database:MigrateOnly`) running as the migrator. So "the API applied a migration by accident" is not
a risk that is managed here — it is a thing the deployment cannot do.

Two runtime operations legitimately need DDL and are therefore `WITH EXECUTE AS OWNER` with `EXECUTE`
revoked from `PUBLIC`: `next_invoice_number` (creates a per-year sequence on first invoice) and
`ensure_audit_log_partitions`. Without that, the first invoice of any year is a 500 and audit
partition maintenance fails silently and permanently — both observed, both fixed, both covered by
tests.

Applying this to a managed instance:
[`docs/runbooks/managed-sqlserver-roles.md`](../runbooks/managed-sqlserver-roles.md).

---

## 4. Environment isolation

A connection string is a value that gets copied, and an application pointed at the wrong environment
works *perfectly* — on the wrong data. Four independent checks, because each catches what the others
cannot:

1. **`DatabaseEnvironmentGuard`** — at startup, above the migrator branch so it covers migrations
   too. `Development` owns `GymOS_Local`, `Staging` owns `GymOS_UAT`, `Production` owns
   `GymOS_Production`, and no host may open another environment's database. Fatal. **There is no
   bypass switch**, because it denies the dangerous pairings rather than prescribing a database per
   environment — so nothing needed a switch that production could also set.
2. **`SqlServerFixture`** — the integration suite refuses to create or open any of the three managed
   names at all.
3. **`Assert-LocalDatabaseTarget`** — the local scripts require `GymOS_Local`, on a local server,
   with `ASPNETCORE_ENVIRONMENT` unset or `Development`. The local-server condition is what catches
   a database *called* `GymOS_Local` sitting on the UAT server.
4. **The deploy workflows** — UAT refuses to migrate anything not named `GymOS_UAT`; Production
   refuses `GymOS_UAT` by name and then requires `GymOS_Production`. Secrets are scoped by GitHub
   Environment, so neither workflow can read the other's.

None of the four ever logs or echoes a connection string. Every message names the environment, the
database found and the database expected — three values already in source control — because the
string itself carries a password. `DatabaseEnvironmentGuardTests` asserts the redaction for every
pairing that can throw.

---

## 5. Secrets — where each kind belongs

| | Local | UAT / Production |
|---|---|---|
| Database connection | `appsettings.Development.json` (public dev password, localhost only) | GitHub Environment secret, injected as `ConnectionStrings__Database`; Container App secret on the running app |
| JWT signing key | public dev key in `appsettings.Development.json` | `GYMOS_JWT_SIGNING_KEY` in the GitHub Environment |
| SMTP, seed admin | `dotnet user-secrets` | environment / Container App secrets |
| Azure deployment | n/a | **no credential at all** — federated OIDC |

Rules that hold everywhere:

- **Nothing sensitive in tracked files.** `.gitignore` covers `.env`, `.env.*` (re-including
  `.env.example`), `appsettings.*.local.json` and `*.user`. The deliberately public development
  values are public *because* they are identical for every developer and reach only localhost;
  removing them would break one-click startup for no security gain.
- **User secrets load only under Development.** A `Staging` or `Production` host does not read them,
  which is why a stale value in a developer's secret store cannot follow them into UAT.
- **`appsettings.Staging.json` carries no connection string and no secrets, and must not gain a
  placeholder one** — a placeholder is how a real value eventually gets committed.
- **Deployment authenticates by federated OIDC with no stored client secret.** Nothing about it
  expires, so nothing needs rotating. The federated subject carries the organisation and repository
  database IDs and the environment name, so a job without its `environment:` line presents a
  different subject and Entra refuses it.

Audited on 2026-08-25 across every tracked file: no live credentials — the only matches are test
constants and the documented public development values.

---

## 6. Container hardening

Non-root (`USER $APP_UID`), read-only where possible, `cap_drop: ALL` with nothing added back, no
`TRUSTWORTHY`, no CLR, no extensions. Only `curl` is added to the runtime image, and only because
the ASP.NET base image ships no HTTP client and a container has to be able to answer "am I healthy?"
about itself. CI asserts the posture rather than trusting it — see the "Assert security posture" step
in `ci.yml` and [`docs/Infrastructure/DOCKER.md`](../Infrastructure/DOCKER.md) §7.

---

## 7. Known gaps

Recorded here so they are decisions rather than oversights. Status:
[`docs/STATUS.md`](../STATUS.md).

- **Application-to-database authentication still uses SQL passwords.** The target is managed
  identity (`id-gymos-api-uat`, `id-gymos-migrator-uat`) with no password anywhere; the migrator half
  requires moving migrations into an Azure Container App job, because a GitHub runner has no Azure
  identity to present. Designed, not applied —
  [`docs/Infrastructure/AZURE.md`](../Infrastructure/AZURE.md) §5.
- **No gym-scoped grant can be issued through the API**, and issuing one against today's permission
  evaluator would be a privilege-escalation defect (`GetEffectivePermissionsAsync` filters on
  `UserId` alone). Designed and approved, not built.
- **ADR-54 (SQL Server) is `Proposed`, not accepted**, while the migration is merged and running UAT.
  A governance gap, not a technical one, and the cheapest item on this list to close.
