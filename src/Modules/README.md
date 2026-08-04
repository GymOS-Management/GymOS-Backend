# Feature Modules

One vertical slice per feature module lands here as its milestone arrives
(Members, Memberships, Scheduling, POS, Gateway, AI, …) — see
ARCHITECTURE-EXTENSIONS.md §2 and §9.

Rules (enforced by GymOS.ArchitectureTests and startup validation):

- A module references **only** `GymOS.SharedKernel`, `GymOS.BuildingBlocks.Application`,
  and other modules' `*.Contracts` projects. Never another module's internals,
  never Platform internals.
- Cross-module communication: **domain events** (default) or **Contracts** queries.
- Each module owns exactly one PostgreSQL schema, named after it.
- Each module declares permissions, navigation, events, jobs, and settings in its
  `IModule` manifest; startup fails fast on validation errors.
- New module projects must be added to the dependency rule table in
  `tests/GymOS.ArchitectureTests` — the build fails until they are.
