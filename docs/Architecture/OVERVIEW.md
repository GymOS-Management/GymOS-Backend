# GymOS backend — architecture overview

The orienting document. It says what the system is, what the pieces are, and where the detail
lives. It deliberately does **not** restate that detail: every section below ends in a link, and
the linked document is the source of truth for its subject. Two documents that explain the same
mechanism drift apart, and then a reader cannot tell which one is wrong.

**The architecture is frozen.** It is specified in
[GymOS-Specification](../../../GymOS-Specification) — `ARCHITECTURE.md`, `ARCHITECTURE-ADDENDUM.md`,
`ARCHITECTURE-EXTENSIONS.md` and the ADR series (repository map: **ADR-52**). Decisions are recorded
there as ADRs, not here; nothing in `docs/` in this repository supersedes them, and there is no
second ADR folder in this repository on purpose.

---

## 1. What it is

One deployable: a **modular monolith** on **.NET 10** over **SQL Server** (ADR-54), serving every
client over HTTP — the Super Admin console (`GymOS-Platform`), the gym web app (`GymOS-Admin`), the
member app, and gateways. There is no second service, no queue broker and no sidecar: the outbox is
a table, advisory locks are `sp_getapplock`, and scheduled work runs in-process.

That is a decision, not a stage. A single process with module boundaries enforced by tests
(`GymOS.ArchitectureTests`) can be split later along boundaries that are already real; a set of
services deployed before those boundaries are known cannot be rejoined.

| Layer | Project | Responsibility |
|---|---|---|
| Domain | `GymOS.Platform.Domain` | Aggregates and invariants. No infrastructure, no EF attributes. |
| Application | `GymOS.Platform.Application` | Commands, queries, handlers, authorization behaviours. |
| Infrastructure | `GymOS.Platform.Infrastructure` | EF Core, persistence, identity, email, tenancy. |
| Host | `GymOS.WebApi` | Composition root, endpoints, middleware. The only deployable. |
| Shared | `GymOS.SharedKernel`, `GymOS.BuildingBlocks.*` | Results, errors, CQRS dispatch, idempotency, jobs. |

Modules land under `src/Modules/` per milestone. The `GymOS.Platform.*` projects are the
platform-scope module area (`ARCHITECTURE-EXTENSIONS.md` §9), unrelated to the `GymOS-Platform`
console repository that shares the word.

---

## 2. The API surface

Versioned by URL segment (`/api/v1/…`, AD-12), documented by an OpenAPI document that is a
**contract**: `contracts/openapi-v1.json` is committed, and `OpenApiContractTests` fails the build
on a breaking diff (AD-37/R13). Additive change is compatible within a version; removing, renaming
or retyping a field needs a new version.

Three endpoints sit outside that contract, and they are the operational surface rather than the
client one:

| Endpoint | Anonymous | In the OpenAPI document | Answers |
|---|---|---|---|
| `/health/live` | yes | no | Is the process alive? Runs **no** checks by design. |
| `/health/ready` | yes | no | Can it serve correct requests? Includes database and pending-migration state. |
| `/version` | yes | no | Which build, which commit, which environment. |

Why they are excluded from the document: they are not part of what a console integrates against,
and publishing them would put operational detail inside the frozen client contract, where every
future change to it would read as an API change. `ApiEndpointTests` asserts the exclusion so it
cannot be lost in a refactor.

Standards for everything inside `/api/v1`: [`docs/Engineering/API_STANDARDS.md`](../Engineering/API_STANDARDS.md).

---

## 3. Data

One database, shared schema, per-module SQL schemas (`platform`, `outbox`), `tenant_id` plus
row-level security for tenancy, month-partitioned audit, a transactional outbox, and optimistic
concurrency on every mutable aggregate.

**EF Core migrations are the only mechanism that changes a database, in every environment.** The
database project (`database/GymOS.Database`) is the declarative record of what those migrations
produce — it validates, compares and produces a DACPAC, and it never publishes.

- The division of responsibility, in full: [`docs/Architecture/DATABASE.md`](DATABASE.md)
- The project itself, and the three objects that are deliberately not comparable: [`database/README.md`](../../database/README.md)
- Tenant isolation as built: [`docs/BusinessRules/TenantIsolation.md`](../BusinessRules/TenantIsolation.md)

---

## 4. Security

Bearer JWT (AD-4), Argon2id password hashing, permission-based authorization evaluated per request,
and a database role split that the application refuses to boot without.

The single most important property: **the process that serves requests cannot change the schema.**
It connects as `gymos_app`, which owns nothing and holds no DDL rights, and `TenantIsolationValidator`
fails startup if that account turns out to be over-privileged. Migrations run as a separate
principal in a separate step.

Full model: [`docs/Architecture/SECURITY.md`](SECURITY.md).

---

## 5. Environments and delivery

```
Development  →  CI  →  UAT  →  Production
   local        every    development     main
  compose       push       branch       branch
```

Three environments, three databases, and no path between them — enforced in four places, one of
which is a startup guard with no bypass switch.

- The environment model: [`docs/Infrastructure/ENVIRONMENTS.md`](../Infrastructure/ENVIRONMENTS.md)
- How a commit reaches UAT and Production: [`docs/Architecture/DEPLOYMENT.md`](DEPLOYMENT.md)
- The Azure resources that exist, and what still does not: [`docs/Infrastructure/AZURE.md`](../Infrastructure/AZURE.md)
- The local stack: [`docs/Infrastructure/DOCKER.md`](../Infrastructure/DOCKER.md)

---

## 6. Observability

Structured logging (Serilog, AD-38) — readable console in Development, compact JSON everywhere
else, because a deployed environment's logs are read by a collector and a developer's are read by a
person. Every request carries a correlation id (`X-Correlation-Id`, minted when absent, never
echoed unvalidated). OpenTelemetry traces and metrics are wired with an OTLP exporter that is off
until an endpoint is configured.

What to look at when something is wrong, and in what order:
[`docs/Runbooks`](../runbooks) — start with
[`INCIDENT-RESPONSE.md`](../runbooks/INCIDENT-RESPONSE.md).

---

## 7. Quality gates

None of these are advisory; all of them fail the build.

| Gate | Where |
|---|---|
| Nullable, warnings-as-errors, analyzers | `Directory.Build.props` |
| Banned APIs (direct clock reads, R2) | `BannedSymbols.txt` |
| Module dependency law (R11/R12) | `GymOS.ArchitectureTests` |
| Endpoint authorization and documentation | `EndpointConventionTests` |
| OpenAPI breaking-change diff | `OpenApiContractTests` |
| Dependency policy and licences | `docs/Engineering/DEPENDENCY_POLICY.md` + its enforcing test |
| Schema parses against Azure SQL | the database project build, in CI |
| UAT schema matches the repository | `.github/workflows/schema-drift.yml` |

---

## 8. Where the numbers are

Implementation status, what was verified and when, and the open items:
[`docs/STATUS.md`](../STATUS.md). It is a working log rather than a reference document — when it and
a document under `Architecture/` disagree about how something works, the reference document is
authoritative and the log is out of date.
