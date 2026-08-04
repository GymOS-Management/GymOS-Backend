# GymOS Dependency Policy

**Status:** Enforced — `tests/GymOS.ArchitectureTests/ForbiddenPackagesTests.cs` fails the
build on any violation. This document and that test must change together, in the same
commit, with the rationale recorded here.
**Related:** AD-1 (no MediatR/AutoMapper, permissive licenses only) · AD-49 (rules R8:
no vendor types outside adapters) · ADR-CHANGELOG.md (change process)

---

## Policy rules

1. **Allowlist, not blocklist.** Every NuGet package must appear in the approved tables
   below (exactly, or under an approved family prefix). Anything else fails the build —
   including packages nobody has thought about yet.
2. **Permissive licenses only:** MIT, Apache-2.0, BSD-2/BSD-3, PostgreSQL, MS-PL.
   **GPL and AGPL are banned** — copyleft obligations are incompatible with commercial
   distribution of GymOS. LGPL is not banned outright but requires an explicit ADR
   (dynamic-linking analysis) before approval.
3. **Layer rules** (mirror of project-reference rules R6–R8):
   - **Domain layer** (`GymOS.SharedKernel`, `*.Domain`, future `*.Contracts`):
     **zero** package references. The domain depends on the language, nothing else.
   - **Application layer** (`*.Application`): abstractions only —
     `Microsoft.Extensions.*.Abstractions`, `Microsoft.Extensions.Options`, `FluentValidation`.
   - **Provider/vendor SDKs** (database drivers, cloud SDKs, payment/messaging vendors):
     Infrastructure projects, the WebApi host, and test projects only.
4. **Central Package Management only.** Versions live in `Directory.Packages.props`;
   a `Version=` or `VersionOverride=` attribute on a `PackageReference` fails the build.
   CPM is also the single audit point for this policy.
5. **Change process:** to approve, ban, or relax anything — one commit that updates
   (a) this document with rationale, (b) the test's policy tables, and (c) if it touches
   a frozen architectural decision, an entry in `docs/ADR-CHANGELOG.md`.

---

## Approved — in use now

| Package | License | Layer | Why |
|---|---|---|---|
| `Microsoft.NET.Test.Sdk` | MIT | tests | Test host |
| `xunit`, `xunit.runner.visualstudio` | Apache-2.0 | tests | Approved test framework (ARCHITECTURE.md §6) |
| `NetArchTest.Rules` | MIT | tests | Architecture rule enforcement (AD-49) |

## Pre-approved — enter with their milestone (already vetted by the frozen spec)

| Package / family | License | Layer | Purpose |
|---|---|---|---|
| `Microsoft.Extensions.*` | MIT | Application (abstractions) / Infrastructure / Host | DI, options, logging, resilience primitives |
| `Microsoft.EntityFrameworkCore.*` | MIT | Infrastructure only | ORM (AD-3) |
| `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql` | PostgreSQL license | Infrastructure only | PostgreSQL provider |
| `EFCore.NamingConventions` | Apache-2.0 | Infrastructure only | snake_case mapping (M0.3) |
| `FluentValidation`, `FluentValidation.DependencyInjectionExtensions` | Apache-2.0 | Application | Command/query validators (AD-10) |
| `Serilog.*` | Apache-2.0 | Host / Infrastructure | Structured logging (AD-38) |
| `OpenTelemetry.*` | Apache-2.0 | Host / Infrastructure | Traces + metrics (AD-38) |
| `Polly` / `Microsoft.Extensions.Resilience` | BSD-3 / MIT | Infrastructure | Resilience pipelines (AD-46) |
| `Konscious.Security.Cryptography.Argon2` | MIT | Infrastructure only | Argon2id hashing (AD-4) |
| `Asp.Versioning.Http` | MIT | Host | API versioning (AD-37) |
| `Testcontainers.*` | MIT | tests | Integration test PostgreSQL (M0.5) |
| `NSubstitute` | BSD-3 | tests | Approved mocking library |
| `Azure.*` | MIT | Infrastructure only | Blob storage, Key Vault adapters (AD-11/AD-20) |

## Banned

| Package | Why banned | Use instead |
|---|---|---|
| `MediatR` (any) | Commercial license from v13 (AD-1); older versions = frozen dead end | In-house `ICommandHandler`/`IQueryHandler` dispatcher |
| `AutoMapper` (any) | Commercial license from v15 (AD-1); implicit mapping hides bugs | Explicit `ToDto()` mapping extensions |
| `Newtonsoft.Json` | Redundant: `System.Text.Json` is the platform standard; duplicate serializers cause subtle contract drift. Approval only via explicit ADR (e.g. a dependency that hard-requires it) | `System.Text.Json` |
| `FluentAssertions` | Commercial license from v8 | xunit assertions |
| `Moq` | SponsorLink telemetry incident eroded trust; ecosystem moved on | `NSubstitute` |
| `Hangfire.*` | Core is LGPL-3.0 + commercial tiers; requires license ADR before the AD-23 adapter decision is made | v1 DB-leased runner; `Quartz` (Apache-2.0) is the pre-cleared adapter candidate |
| `MassTransit` (v9+) | Announced commercial licensing | Outbox + future broker adapter (AD-13) is our own seam |

## Licensing notes

- **Why GPL/AGPL are banned:** GymOS is commercially distributed, closed-source software.
  GPL-family copyleft would obligate source disclosure; AGPL triggers even on
  network use — an unacceptable risk for a SaaS. This restates company policy.
- **Commercial-pivot watch:** the .NET OSS ecosystem has seen several MIT→commercial
  relicensings (MediatR, AutoMapper, FluentAssertions, MassTransit). Pinned old versions
  are not a strategy — they freeze you out of security fixes. When a dependency pivots,
  raise an ADR: replace, buy, or fork.
- **Transitive dependencies:** the build-time test governs *direct* references.
  `CentralPackageTransitivePinningEnabled` pins transitive versions; a full transitive
  license audit (`dotnet list package --include-transitive`) is a CI-preparation item (M0.5).
- **One audit point:** because of CPM, `Directory.Packages.props` at the repo root is
  the complete inventory of direct dependencies at any moment.
