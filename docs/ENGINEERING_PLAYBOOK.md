# GymOS Engineering Playbook

**Status:** Standing rules for GymOS-Backend. Where a rule has an automated enforcer, the enforcer is
named — a rule without one is a suggestion, and this document tries not to contain suggestions.
**Related:** [API_STANDARDS.md](Engineering/API_STANDARDS.md) ·
[DEPENDENCY_POLICY.md](Engineering/DEPENDENCY_POLICY.md) · [STATUS.md](STATUS.md) ·
[RELEASE-NOTES.md](RELEASE-NOTES.md) · GymOS-Specification (frozen architecture)

---

## 1. Branch strategy

Trunk-based. `main` is always releasable.

| Branch | Purpose | Lifetime |
|---|---|---|
| `main` | The trunk. Protected. Every commit builds and passes the full suite. | permanent |
| `feature/<short-description>` | One coherent change. | days, not weeks |
| `fix/<short-description>` | A defect fix. | hours |
| `chore/<short-description>` | Dependencies, tooling, docs. | hours |

- **Short-lived branches.** A branch open longer than a few days is a merge problem accumulating
  interest. Split the work instead.
- **Rebase on `main` before opening a PR**; merge with a merge commit so the PR remains identifiable
  in history.
- **No long-running integration branch.** Incomplete work hides behind configuration, not behind a
  branch — that is what makes trunk-based development safe.
- Milestone work may land directly on `main` while the platform has no consumers. **From the first
  generated client onward, everything goes through a PR** — the contract needs a reviewer.

## 2. Commit message convention

```
<area>: <imperative summary, ≤ 72 chars>

Why this change, and what it does. Wrap at 88. Explain the reasoning a
reader cannot recover from the diff — the diff already shows the what.

Co-Authored-By: … (when applicable)
```

`<area>` is a milestone (`M1.3`), a module (`identity`), or a concern (`docs`, `ci`, `deps`).

- **The body explains why.** "Fix bug" is not a commit message; "the empty-password path faulted an
  unauthenticated endpoint because Konscious rejects zero-length input" is.
- **One logical change per commit.** A commit that both moves code and changes behavior is a commit
  nobody can review or revert.
- **Reference the decision, not the ticket** — `AD-4`, `§2.6`, `R11`. Those outlive any tracker.
- **No `wip`, no `fixup` on `main`.** Squash before the PR merges.

## 3. PR checklist

Author, before requesting review:

- [ ] `dotnet build GymOS.Backend.slnx -c Release` — clean, warnings are errors
- [ ] `dotnet test GymOS.Backend.slnx` — all green (Docker required)
- [ ] `dotnet format --verify-no-changes`
- [ ] `dotnet ef migrations has-pending-model-changes` — none, or the migration is in this PR
- [ ] New/changed endpoints: summary, description, typed responses, `ProducesProblem` per failure
- [ ] OpenAPI snapshot regenerated **and the diff read**, not just accepted
- [ ] New dependency: `DEPENDENCY_POLICY.md` **and** `ForbiddenPackagesTests` updated in this commit
- [ ] New mutable entity: concurrency token, or an entry in `ExemptFromConcurrency` with a reason
- [ ] New command/query: `[RequiresPermission]` or `[NoPermissionRequired]` with a justification
- [ ] Tests cover the failure paths, not only the happy path
- [ ] `STATUS.md` updated if this changes how someone resumes work
- [ ] Deviation from the frozen architecture? Superseding ADR first — see §5

## 4. Code review checklist

Reviewer. In this order, because the later items do not matter if an earlier one is wrong.

**Correctness**
- Does it do what the description claims? Is there a case the tests do not cover?
- Are failure paths handled, or only the happy path?
- Concurrency: could two callers race this? Is the aggregate's version advanced when children change?
- Time: read from `IClock` at the edge and passed inward, never read mid-logic?

**Architecture**
- Right layer? Domain free of infrastructure, Application free of EF, no vendor type outside an adapter.
- Business failure as `Result`, invariant breach as `DomainException`. Not swapped.
- Cross-aggregate rule in the Application layer, not handed a repository inside an aggregate.

**Security**
- Permission declared on both layers? Deny by default?
- Anything secret in a log, an audit row, or a response? Redaction covers new sensitive fields?
- Is an error message more specific than it should be — does it tell an attacker something?

**API**
- Matches API_STANDARDS.md: status codes, envelope, naming, `ETag`, problem shape.
- Additive within v1, or does it need a new version?

**Maintenance**
- Would a new engineer understand *why* from the code and its comments?
- Does a comment explain reasoning, or restate the line below it?
- Is duplication real (extract) or coincidental (leave it)?

**What not to review:** formatting (the formatter decided), and style preferences the analyzers do
not enforce. Say "consider" for those and move on.

## 5. ADR process

The architecture is **frozen** (ADRs 1–49 across the GymOS-Specification set). It changes one way.

1. **Check the register first.** GymOS-Specification/ADR-CHANGELOG.md is the amendment log; the frozen
   documents are read *as amended by it*.
2. **Write a superseding ADR** before any implementation follows it, stating: the issue, the options
   considered, the trade-offs, the decision, and the consequences. ADR-50 and ADR-52 are the templates.
3. **Get it approved**, then implement.
4. **An implementation detail is not an ADR.** Choosing a server-maintained `rowversion` over a hand-bumped column,
   or an anonymous projection over a record, needs a comment — not an amendment.
5. **A deviation forced by another approved rule is documented, not resolved by an ADR.** Example:
   AD-3's `IAppDbContext` is impossible because R7 bans EF Core in the Application layer; the
   substitute (`IUnitOfWork` + repositories) preserves the intent and is recorded in the release notes.

If you are unsure whether something needs an ADR: if a future engineer could be surprised by it and
would want to know who decided and why, it needs one.

## 6. Definition of Done

A change is done when **all** of the following are true. Not "when the code works".

- [ ] It does what was asked, including the parts that were tedious
- [ ] Failure paths are implemented and tested
- [ ] The full suite passes locally, not just the tests you wrote
- [ ] Release build is clean with warnings as errors
- [ ] New behavior is documented where a reader will look for it: XML docs on the seam, and
      `STATUS.md`/release notes if it changes how the platform is operated
- [ ] The OpenAPI contract is regenerated and its diff reviewed
- [ ] Anything deliberately left undone is written down, with the reason — not left to be rediscovered
- [ ] No new dependency without a policy entry, no new endpoint without a permission, no new entity
      without a concurrency decision

"Done" explicitly does **not** require: perfect coverage, zero technical debt, or a feature the
milestone did not ask for.

## 7. Testing requirements

| Layer | What it covers | Speed |
|---|---|---|
| Domain unit | Aggregates, state machines, business rules, value objects. Pure, no fakes needed. | ms |
| Application unit | Handlers through the real dispatcher and pipeline, with in-memory fakes at the seams. | ms |
| Infrastructure unit | Adapters that need no external dependency: crypto, tokens, redaction, the EF model. | ms |
| Architecture | Layer and package law, endpoint and event conventions. Runs on every build. | ms |
| Integration | Real HTTP + real SQL Server (Testcontainers): flows, permissions, migrations, physical layout. | seconds |

**Rules**

- **Test behavior, not implementation.** Assert on the state a handler produced, not on which methods
  it called — otherwise every refactor breaks the suite for no reason.
- **Resolve through the container** in Application tests. A handler that exists but was never
  registered should fail a test, not production.
- **Every test isolates its data.** xUnit builds a fresh class instance per method, so a shared
  database name silently couples tests and makes row-counting order-dependent. Use
  `CreateIsolatedDatabaseAsync` where the assertions depend on the rows present.
- **A convention worth having is a test, not a paragraph.** R11, R12, R13, R9, and the concurrency
  model are all enforced by tests that a new module inherits with nothing to write.
- **Assert the failure message, not just the status.** A helper that fails with a bare status code
  sends the next reader hunting through logs.
- **Time is injected.** `FakeClock` and advance it; never `Task.Delay` in a test.
- **No test asserts on a secret's value** — assert it is redacted.

Coverage is not a target. The question is whether the *failure paths* are covered, and no percentage
answers that.

## 8. Logging standards

- **Source-generated `LoggerMessage`** for every log statement. No interpolated strings: they allocate
  on suppressed levels and lose the structure that makes a log queryable.
- **Structured properties, not sentences.** `LogSignInFailed(logger, reason, email, ip)`, not
  `$"Sign-in failed for {email}"`.
- **Levels mean things.**

| Level | Use |
|---|---|
| `Error` | A defect, or something needing human action. A permission check that reached the pipeline. |
| `Warning` | Degraded but handled: a skipped seed, an unbuilt partition, a rejected sign-in. |
| `Information` | State changes worth reconstructing later: a successful sign-in, a created account. |
| `Debug` | Detail for diagnosis. Off in production. |

- **Never log a secret.** No password, hash, raw token, signing key, or security stamp — in any level,
  including `Debug`. The audit redaction policy covers persisted rows; logs are on the author.
- **Never log to explain a security answer.** If the response is deliberately vague, the reason goes in
  the log or the audit trail, not both places the caller can see.
- **Correlation is automatic.** `X-Correlation-Id` is pushed into the log context for the whole
  request; do not pass it by hand.
- **Log an exception object, not its message** — the stack trace is the useful part.

## 9. Naming conventions

**C#** — standard .NET conventions, enforced by analyzers and `dotnet format`. Beyond those:

| Thing | Convention | Note |
|---|---|---|
| Seam (interface) | `I<Capability>` | `IPasswordHasher`, not `IPasswordHasherService` |
| Adapter | names the technology | `Argon2PasswordHasher`, `JwtTokenService` |
| Command | imperative | `SuspendUserCommand` |
| Query | `Get`/`List` | `ListUsersQuery` |
| Handler | `<Request>Handler` | `SuspendUserCommandHandler` |
| Domain event | past tense | `UserSuspended` |
| Timestamp | `…AtUtc` | Everything stored is UTC (AD-40) |
| Options | `<Area>Options` + `SectionName` | `JwtOptions` |
| Test | `MethodOrScenario_StatesTheExpectedOutcome` | Reads as a sentence in the runner |

Avoid `Manager`, `Helper`, `Utility`, `Service` as a suffix that means nothing. If a type needs one to
sound like it does something, it probably does too much.

**Database** — `snake_case` by convention (`EFCore.NamingConventions`), plural tables, one schema per
module. Never hand-write a column name that the convention would produce differently.

## 10. Migration policy

- **Generated, never hand-edited after merge** (R17). Editing a merged migration means environments
  disagree about what has been applied.
- **Hand-authored bodies are allowed for DDL EF cannot express** — partitioning, functions, RLS — and
  must be commented with why. `PartitionAuditLogs` is the reference.
- **Additive-preferred.** Add a nullable column and backfill; do not rewrite a table because it would
  be tidier.
- **Destructive change is two deployments.** Stop writing, deploy, then remove — never one migration
  that drops a column the running version still reads.
- **Data migrations are non-destructive**: rename aside, copy, drop. Never `DROP` before the copy
  succeeded, so a rollback leaves the original in place.
- **Verify the model matches**: `dotnet ef migrations has-pending-model-changes` is in the PR checklist.
- **Production applies migrations from the pipeline**, not from application startup.
  `Database:MigrateOnStartup` is a development convenience.
- **Every migration is applied twice in CI** (`MigrationTests`), so a non-idempotent one fails before
  it reaches an environment.

## 11. Release process

1. `main` is green: Release build, full suite, format, dependency audit, contract snapshot.
2. Update `docs/RELEASE-NOTES.md` — Added, Changed, Fixed, Known Limitations, Deferred Items,
   Architecture Compliance, Testing Summary. **Known Limitations is not optional**: a limitation
   discovered by a user that was known to the team is a trust problem, not a bug.
3. Update `docs/STATUS.md` so someone resuming on another machine is not guessing.
4. Commit along reviewable boundaries — one concern per commit, each independently buildable.
5. Tag annotated: `git tag -a vX.Y.Z`, message summarizing highlights, verification state, and
   limitations.
6. Push branch and tag.
7. Deploy: migrations from the pipeline, then the application.
8. Verify against the running system, not against the test suite.

**Rollback:** redeploy the previous tag. Because migrations are additive, the previous version runs
against the new schema — which is the whole reason for the additive rule.

## 12. Versioning strategy

**Semantic versioning**, with the pre-1.0 phase meaning what it says.

- **`0.x.y` — now.** The API has no consumers, so it is still shapeable. Minor per milestone
  (`0.1.0` M0, `0.2.0` M1.1–M1.2, `0.3.0` M1 complete), patch for fixes between them.
- **`1.0.0`** is the first release a client depends on. From then, `openapi-v1.json` is a promise.
- **After 1.0:** MAJOR for a breaking change to a *supported* API version; MINOR for additive API or
  new features; PATCH for fixes.
- **The API version and the product version are independent.** `/api/v1` can survive many product
  majors; a v2 route appears when one endpoint needs a breaking change, not when the product does.
- **Deprecation** (AD-37 §6.2): `Deprecation` and `Sunset` headers, a successor `Link`, at least six
  months, and no removal while measurable traffic remains without explicit sign-off.
- **Tags are immutable.** A tag never moves; a mistake gets a new patch version.
