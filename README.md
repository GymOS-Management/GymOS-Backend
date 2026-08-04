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
dotnet run --project src/Host/GymOS.WebApi       # http://localhost:5080/health/ready
```

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
