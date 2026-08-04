# GymOS-Backend

**The** GymOS backend — the entire server side as one modular monolith
(`.NET 10 / PostgreSQL`), per the frozen architecture in
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
```

Note: the `GymOS.Platform.*` project names refer to the platform-scope module area
inside this backend (ARCHITECTURE-EXTENSIONS.md §9) — unrelated to the GymOS-Platform
frontend repository.

## Local development

```
docker compose up -d --wait                      # PostgreSQL 17 (add --profile redis for Redis)
dotnet ef database update --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi

# An account to sign in with. Skip it and the seeder warns and creates nothing —
# GymOS never ships a default credential.
dotnet user-secrets set "Seed:SuperAdmin:Email" "you@example.com"  --project src/Host/GymOS.WebApi
dotnet user-secrets set "Seed:SuperAdmin:Password" "<12+ chars>"   --project src/Host/GymOS.WebApi

dotnet run --project src/Host/GymOS.WebApi       # http://localhost:5080/health/ready
```

Then sign in against `POST /api/v1/auth/login`; the API surface is at
`http://localhost:5080/openapi/v1.json` (`/auth`, `/users`, `/roles`, `/audit-logs`). The
development JWT signing key lives in `appsettings.Development.json` and is public by
design — every other environment must supply `Security:Jwt:SigningKey` (startup fails fast
without it).

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
