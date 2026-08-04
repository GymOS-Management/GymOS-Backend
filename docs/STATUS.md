# GymOS — Implementation Status & Continuation Notes

**Last updated:** 2026-08-04 · after M1.1 (commit `93cf585`)
**Purpose:** cross-device continuation state. Read this + `GymOS-Specification/README.md`
before resuming work on any machine.

---

## Where we are

| Milestone | State |
|---|---|
| Architecture (ADRs 1–49, frozen) + amendments (ADR-50 .NET 10, ADR-51 superseded, ADR-52 repo map) | ✅ in GymOS-Specification |
| **M0 — engineering foundation** (skeleton, architecture/package law, kernel + CQRS dispatcher, PostgreSQL/EF/outbox, Docker, logging/correlation/OTel, Testcontainers, CI) | ✅ complete |
| **M1.1 — identity domain** (User/Role/Permission/RefreshToken aggregates, platform schema migration, declare-and-validate seeder) | ✅ complete — **69 tests green** |
| **M1.2 — auth services & endpoints** | ⬅ **NEXT** |
| M1.3 — permission pipeline, users/roles endpoints, audit interceptor, OpenAPI diff gate | after M1.2 |
| M2+ — per approved roadmap (gyms, subscriptions, …) | later |

## M1.2 scope (agreed, not started)

1. **Argon2id password hasher** — `Konscious.Security.Cryptography.Argon2` (pre-approved
   in DEPENDENCY_POLICY.md), parameters from configuration, `IPasswordHasher` seam in
   Application, implementation in Platform.Infrastructure.
2. **JWT access tokens** (15 min; claims: sub, email, permissions hash, security_stamp)
   + **rotating refresh tokens**: opaque, SHA-256 hashed at rest (entity already exists),
   httpOnly Secure cookie, rotation with chain-revocation reuse detection (AD-4).
3. **`/api/v1/auth` endpoint group** via `Asp.Versioning.Http` (pre-approved):
   login (rate-limited per IP+email), refresh, logout, me, change-password.
4. **Dev-only initial SuperAdmin seed** — password from configuration
   (`Seed:SuperAdmin:Email/Password`, user-secrets), hashed via the new hasher; seeder
   skips with a warning when unset. Never a hardcoded credential.
5. Integration tests for the full login → refresh → reuse-detection → logout flow.

## Standing rules (non-negotiable, enforced)

- **Architecture is frozen.** Deviations need a superseding ADR in
  `GymOS-Specification/ADR-CHANGELOG.md` *before* implementation (ADR-50/52 are the
  templates). Do not silently modify decisions.
- **Increment protocol:** explain design → implement → verify (build, tests, live run)
  → summarize → **stop for review**. Never jump ahead.
- **Dependency policy:** `docs/Engineering/DEPENDENCY_POLICY.md` and
  `ForbiddenPackagesTests` change **in the same commit**. Allowlist, not blocklist.
- **Golden Rule** (`GymOS-Specification/PRODUCT-PRINCIPLES.md`): every feature must
  reduce work for the gym owner; trim scope per module before implementing.
- House patterns already established: `IClock` only (BCL clocks are analyzer-banned);
  time flows inward as parameters; business failures are `Result`, invariant breaches
  are `DomainException`; events raised in aggregates only; source-generated
  `LoggerMessage` for logging; migrations never hand-edited (generated-code editorconfig).

## Repo map (ADR-52) — org: github.com/GymOS-Management

GymOS-Backend (this repo — THE .NET modular monolith, active) · GymOS-Specification
(frozen docs) · GymOS-Platform (Super Admin Next.js app, not started) · GymOS-Admin
(gym staff app) · GymOS-Mobile · GymOS-Gateway · GymOS-Website.
Note the trap: `GymOS.Platform.*` *projects in this repo* = platform-scope modules,
unrelated to the GymOS-Platform frontend repo.

## Fresh-machine setup

```
git clone https://github.com/GymOS-Management/GymOS-Backend
cd GymOS-Backend
dotnet tool restore                # pinned dotnet-ef
docker compose up -d --wait        # PostgreSQL 17
dotnet test GymOS.Backend.slnx     # expect 69 green (needs Docker for integration tests)
dotnet run --project src/Host/GymOS.WebApi   # migrates + seeds in Development; :5080/health/ready
```

Environment facts: .NET 10 SDK pinned via `global.json` (ADR-50); solution is
`GymOS.Backend.slnx`; local dev DB credentials live in `appsettings.Development.json`
matching `docker-compose.yml` (dev-only).

## Known open items / honest notes

- Outbox **processor** (delivery to handlers with SKIP LOCKED claiming) not built yet —
  capture side only; lands with the first event handler need.
- Outbox `user_id` column is stamped null until auth context exists (M1.2/M1.3);
  `correlation_id` is live.
- Audit-log interceptor (jsonb old/new diffs) is M1.3, not yet present.
- OpenAPI CI diff gate is stubbed in `.github/workflows/ci.yml`, activates with the
  first versioned endpoints (M1.2).
- EF warning "No IEntityTypeConfiguration found in GymOS.Platform.Infrastructure" is
  gone since M1.1 (configurations now exist).
