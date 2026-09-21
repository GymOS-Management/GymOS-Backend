# GymOS — Cross-Repo Roadmap

**Written:** 2026-09-21 · **Basis:** measured velocity from git, not a plan that existed before this file.
**Authority:** PRODUCT-PRINCIPLES.md (the Golden Rule) is the scope authority over everything below.
**Canonical status stays** `GymOS-Backend/docs/STATUS.md`. This file is sequencing, not status.

It lives here, in the backend repo, because this is the repository every client already references and
because most slices below are sequenced by backend work. It describes all seven repositories, not this
one — so treat the path prefixes as workspace-relative.

---

## 1. The organizing principle: slices, not repos

The current state is what happens without this rule. The backend is at M7 + H1 with 44.5k LOC and
1,640 tests; `GymOS-Admin`, `GymOS-Mobile` and `GymOS-Gateway` are one README each. The backend ran
nine milestones ahead of three of its five clients.

**Per ADR-52 there is one backend, not two services.** Gym-scope modules land in
`GymOS-Backend/src/Modules/` inside the same modular monolith as the platform-scope ones — no second
service, no cross-repo NuGet dance, no contract versioning between backend halves. The repo split is
by *deliverable*, and the only split that matters below is backend ↔ its five clients.

So the unit of work is **the slice, not the milestone**: one capability, taken from spec to every
surface that needs it, before the next slice starts.

**A slice is not done when the backend ships it. It is done when every repo in its row is done.**

### Propagation order inside every slice

Fixed, and in this order:

| # | Step | Repo | Why it is where it is |
|---|---|---|---|
| 1 | Design doc + task plan | `GymOS-Backend/docs/superpowers/` | The 1.3-day build rate only ever happened behind a spec. Skipping this is what makes an estimate meaningless. |
| 2 | Domain → application → API → permission → manifest → migration | `GymOS-Backend` | Migration policy: additive-preferred, destructive change is two deployments (Playbook §10). |
| 3 | Regenerate `contracts/openapi-v1.json`, **read the diff** | `GymOS-Backend` | Playbook §6 Definition of Done. |
| 4 | `pnpm gen:api` in every consuming client, read the type diff | each client | This is the propagation moment. A slice that changes the contract changes every client that reads it. |
| 5 | Build the surfaces | Platform / Admin / Mobile / Gateway / Website | Only the ones in the slice's row. |
| 6 | As-built doc, `STATUS.md`, release notes | Backend + the client repos | Playbook §6. |

### Propagation checklists — when a slice adds X, X also lands in Y

This is the "finish once, apply everywhere" mechanic, made explicit so it is not rediscovered per slice.

| When a slice adds… | It must also land in… |
|---|---|
| **An endpoint** | The OpenAPI contract; client types in every consuming repo; a permission (never an endpoint without one); an audit entry if it mutates |
| **A permission** | Identity seed; role grants; the UI hide rule in each client; `GET /navigation` |
| **A navigation entry** | The backend navigation endpoint (permissions ∧ feature flags); the shell in Platform and Admin |
| **A feature flag** | The `features` catalog seed; plan-feature mapping; the gym override path; the resolution endpoint; client-side gating |
| **A setting** | The declared catalog (typed + ranged); the Settings screen in the Platform console; the consuming options class |
| **An entity** | A concurrency decision (Playbook §6); a migration; the `database/GymOS.Database` project; `schema-compare.ps1` |
| **A domain event** | The event catalog (EXTENSIONS §1.3); outbox dispatch; every subscribing module; the automation engine's trigger list once S12 exists |
| **A job** | The job catalog; `IRegisteredJobs`; a row on the monitoring page; a retention rule |
| **A new module** | Its `IModule` manifest; its own SQL Server schema; the `GymOS.ArchitectureTests` dependency table (the build fails until it is there) |
| **Anything a gym owner sees** | The Golden Rule test — name the work it removes, or cut it |

---

## 2. Phase 0 — Enablers

Nothing here is user-facing. Everything after it is blocked until these land, so they are not optional
and they are not background work.

| ID | Work | Repos | Units |
|---|---|---|---|
| **E1** | **Module system runtime.** `src/Modules/` holds a README and nothing else. The `IModule` manifest, startup validation, per-module schema ownership and the `GymOS.ArchitectureTests` dependency table are specified (EXTENSIONS §2) and unbuilt. Every slice from S1 on lands as a module, so this is the true first task. | Backend | 1.5 |
| **E2** | **Tenant identity.** `platform.users` is platform administrators only — `Identity/` holds `User`, `Role`, `Permission` and nothing gym-side. Gym staff and members live in tenant identity, which does not exist. **Nobody can sign in to Admin or Mobile until it does.** Carries gym-scoped-grants steps 3–5. | Backend | 2 |
| **E3** | **`GET /navigation` + the owed list.** Navigation and feature visibility come from the backend (Admin README). Plus the H1 leftovers, the ~19 non-severe M1–M5 findings, the integration teardown cost, the three dependency majors, and the owner items (Key Vault, ADR-54 rationale). | Backend, Platform | 1.5 |
| **E4** | **Contract distribution for CI.** `contracts/openapi-v1.json` is already committed, and generated types are committed in the client — so this works today. The gap is narrow: `gen:api` and the CI drift check read `../GymOS-Backend/...`, a sibling checkout, guarded by an `if [ -f ]` that silently skips when it is missing. That guard turns a real drift failure into a green build. Worth closing before a second and third client depend on it. | Backend, Platform | 0.5 |

**Phase 0: 5.5 units.** E1 and E2 are the hard prefix — nothing in Phase 1 can start without them.
E4 is small and can be done any time before Phase 1's client work.

**Not here, deliberately:** BuildingBlocks → NuGet. ADR-52 is explicit — BuildingBlocks never need
publishing for backend consumers, because they are the same solution. A feed is needed only when
`GymOS-Gateway` starts consuming SharedKernel contracts, so it sits in Phase 5 with S15.

---

## 3. Phase 1 — Members core

The frozen `MEMBERS-DOMAIN-DESIGN.md` (1,589 lines, approved 2026-08-05) covers S1–S3. **This is the
only gym-facing work that already has its product decisions made** — which is exactly why it goes first.

| ID | Slice | Backend | Platform | Admin | Mobile | Gateway | Website | Units |
|---|---|---|---|---|---|---|---|---|
| **A0** | Admin app foundation — Next.js shell, sign-in against tenant identity, navigation from `GET /navigation` | — | — | ● | — | — | — | 1.5 |
| **S1** | Members — profile, relationships, lifecycle (§4, §5, §15) | ● | — | ● | — | — | — | 3 |
| **S2** | Memberships — types, renewal, grace, cancel / upgrade / downgrade / transfer (§6–§10) | ● | — | ● | — | — | — | 3 |
| **M0** | Mobile app foundation — shell, auth, contract client | — | — | — | ● | — | — | 2 |
| **S3** | Attendance, check-in and the access decision (§11–§13) | ● | — | ● | ● QR | ○ deferred | — | 3.5 |

**Phase 1: 13 units.** S3's offline path is deliberately deferred to S15 — the accepted limitation in
§11.10 (an offline gateway may admit someone whose membership changed moments earlier) must be put to
the gym owner in writing before a live gym relies on it.

**Three legal items** (§20.1–§20.4) need values from counsel before a live gym uses S1–S3. They do not
block building — the model is shaped so they stay additive — but they do block go-live, and under
GDPR and Cyprus law the retention and erasure values in particular should be confirmed with legal
counsel rather than chosen in code.

---

## 4. Phase 2 — Daily operations

This is the end of the sellable-MVP line. A gym that has Phases 0–2 can run its day.

| ID | Slice | Backend | Platform | Admin | Mobile | Gateway | Website | Units |
|---|---|---|---|---|---|---|---|---|
| **S4** | Scheduling — templates vs. materialized occurrences, capacity, waitlists, conflicts (PLATFORM-CAPABILITIES §1) | ● | — | ● | ● booking | — | ● public timetable (§1.7) | 4 |
| **S5** | POS + Payments | ● | — | ● | — | — | — | 4 |
| **S6** | Reports | ● | — | ● | — | — | — | 2 |

**Phase 2: 10 units.** S4 is the first slice that touches four repos at once — it is the real test of
whether the propagation discipline in §1 holds.

> **MVP line: Phases 0–2 = 28.5 units.**

---

## 5. Phase 3 — Growth and retention

| ID | Slice | Backend | Admin | Mobile | Units |
|---|---|---|---|---|---|
| **S7** | CRM + marketing | ● | ● | — | 3 |
| **S8** | Inventory | ● | ● | — | 2 |
| **S9** | Trainer, workout builder, nutrition | ● | ● | ● | 4 |
| **S10** | Notification centre + announcements (PLATFORM-CAPABILITIES §4.1–§4.2) | ● | ● | ● | 2.5 |

**Phase 3: 11.5 units.** Every one of these is a `features` catalog key already seeded — so each is a
feature flag, a plan mapping and a gating rule in two clients, per the checklist in §1.

---

## 6. Phase 4 — Platform capabilities

| ID | Slice | Repos | Units |
|---|---|---|---|
| **S11** | Search — projected documents behind a seam (§5) | Backend, Admin, Platform | 2 |
| **S12** | Automation engine (§2) — consumes every domain event the slices above published | Backend, Admin | 3 |
| **S13** | Integrations module (§3) — two-tier, webhook inbox | Backend, Admin | 3 |
| **S14** | AI abstraction layer (EXTENSIONS §3) | Backend, Admin | 2.5 |

**Phase 4: 10.5 units.** S12 is why the domain-event checklist row matters from S1 onward: an automation
engine is only as good as the events the earlier slices remembered to publish.

---

## 7. Phase 5 — Hardware

| ID | Slice | Repos | Units |
|---|---|---|---|
| **S15a** | **BuildingBlocks → NuGet.** The one point where ADR-52 says a feed is required: `GymOS-Gateway` consumes `GymOS.SharedKernel` contracts across a repo boundary (ADR-51 §consequences, carried forward). | Backend, Gateway | 1 |
| **S15b** | Gateway — cloud module first, then the on-prem agent, the HAL, and `SimulatedHardwarePlugin` as the first deliverable so every flow is testable with zero physical hardware | Backend, **Gateway**, Admin | 5 |
| **S16** | Access control + device registry | Backend, Gateway, Admin | 2 |

**Phase 5: 8 units.** This phase depends on vendors and physical devices, so code velocity does not
predict it. Treat the estimate as the software share only.

---

## 8. Phase 6 — Public

| ID | Slice | Repos | Units |
|---|---|---|---|
| **S17** | Marketing site; hosts the public timetable surface from S4 if SEO needs server-rendering outside the product apps | **Website** | 2 |

**Phase 6: 2 units.** Unscheduled by the workspace README, and genuinely parallel — it couples to
nothing except the S4 public timetable API. It can be done by someone else at any point.

---

## 9. What this costs

**One unit ≈ one milestone.** Calibrated against delivered work: M0–M7 + H1 = 9 units in 48 days,
producing 44,552 LOC of source, 32,504 LOC of tests, 1,640 passing tests, 15 projects, 8 migrations.

Two rates, both measured, both real:

- **Focused build: ~1.3 days per unit.** M0–M5 took 8 days. M6, M7 and H1 took 3.
- **All-in calendar: ~5.3 days per unit.** Of 48 elapsed days, ~15 were feature-building. The
  Neon→SQL Server migration, the SaaS hardening increment and three weeks of red CI took the rest.
  **About 31% of calendar time converts to features.**

| | Units | At 5.3 d/unit | Pipelined (−25%) |
|---|---|---|---|
| Phase 0 | 5.5 | 4 weeks | 4 weeks (hard prefix, cannot pipeline) |
| Phases 0–2 (**MVP**) | 28.5 | 22 weeks | **16 weeks → ~mid-Jan 2027** |
| Phases 0–4 | 50.5 | 38 weeks | 29 weeks → ~Apr 2027 |
| **Everything** | **60.5** | **46 weeks** | **34 weeks → ~May 2027** |

"Pipelined" assumes the backend of slice N+1 is specced and built while the clients of slice N are being
built — which is the one structural speed-up available, and the reason slices are sequenced rather than
phased per repo. Phase 0 cannot be pipelined; it is a hard prefix.

**Plan against ~Q1 2027 for the MVP line and mid-2027 for everything.** The optimistic reading (no
further infrastructure shocks, ~2.5 d/unit) lands the MVP in January and everything around March — I
would not commit to it, because two of the last seven weeks' three biggest costs were unplanned.

### The three things that move these numbers

1. **Specs, not typing.** Only the Members cluster has its product decisions made. Every other slice
   needs a design doc before its build rate is anything like 1.3 days per unit. The spec work is the
   unpriced part of Phases 2–5.
2. **The conversion rate.** 31% is the whole estimate. Two more incidents the size of the SQL Server
   migration add roughly two months, and no amount of build speed compensates.
3. **The Golden Rule cut.** Phases 3–5 are 30 units of "everything the architecture describes". Taken
   against the rule — *does this reduce work for the gym owner?* — a serious cut could plausibly halve
   them. That cut is a product decision, and it is worth making before S7, not during it.
