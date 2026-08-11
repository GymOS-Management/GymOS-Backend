# M4.1 — Feature-flag review fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the M4 whole-increment review's Critical and Important findings, and the documentation that claims what the code does not do.

**Architecture:** A redundant override is one where `FeatureResolution.RequiresOverride` — the function the write path already uses — returns false. A pure domain service applies that rule to a gym's stored exceptions; an application service withdraws them. It is called **synchronously** by every path that changes one gym's subscription, and by a **nightly sweep** for the platform-wide triggers whose blast radius is every gym on a plan.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, M3.4's `IScheduledJob`/`JobHost`, xUnit + Testcontainers.

**Design:** `docs/superpowers/specs/2026-08-12-m4-review-fixes-design.md` (approved 2026-08-12). Where this plan and the design disagree, the design wins.

## Global Constraints

- **Architecture is frozen.** Deviations need a superseding ADR first. This increment makes none.
- **The redundancy rule is not re-expressed.** Everything routes through `FeatureResolution.RequiresOverride`. A second definition of "redundant" is a Critical finding in its own right.
- `IClock` only — BCL clocks are analyzer-banned; background sweeps use `IDatabaseClock`.
- Business failures are `Result`; invariant breaches are `DomainException`. Events are raised in aggregates only.
- Logging is source-generated `LoggerMessage`.
- **`TreatWarningsAsErrors` is on.** A build with warnings is a failed build.
- **Test runs must be COMPLETE** — no `Aborted` in the output, totals must match. The host crashes intermittently on this machine; re-run rather than debugging the named test.
- Docker must be running for integration tests and the live run.
- Baseline before this plan: **1304** (SharedKernel 126 · BuildingBlocks 62 · Architecture 151 · Domain 414 · Application 232 · Infrastructure 67 · Integration 252).

---

### Task 1: The redundancy rule as a pure function

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Features/GymFeatureExceptions.cs`
- Test: `tests/GymOS.Platform.Domain.UnitTests/GymFeatureExceptionsTests.cs`

**Interfaces:**
- Produces:
  - `record StoredException(Guid FeatureId, bool IsEnabled)`
  - `GymFeatureExceptions.Redundant(IEnumerable<StoredException> stored, IReadOnlyDictionary<Guid, bool> planOpinions, IReadOnlyDictionary<Guid, bool> platformDefaults)` → `IReadOnlyList<Guid>`

- [ ] **Step 1: Write the failing test**

```csharp
using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class GymFeatureExceptionsTests
{
    private static readonly Guid Crm = Guid.NewGuid();
    private static readonly Guid Pos = Guid.NewGuid();

    [Fact]
    public void AnOverrideThePlanNowGrantsIsRedundant()
    {
        // The Critical finding, as one assertion: the gym was granted CRM as an exception on a plan
        // that excluded it, then upgraded to a plan that includes it.
        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool> { [Crm] = true },
            platformDefaults: new Dictionary<Guid, bool> { [Crm] = false });

        Assert.Equal(Crm, Assert.Single(redundant));
    }

    [Fact]
    public void AnOverrideThatStillDiffersSurvives()
    {
        Assert.Empty(GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool> { [Crm] = false },
            platformDefaults: new Dictionary<Guid, bool> { [Crm] = false }));
    }

    [Fact]
    public void WithNoPlanOpinionTheDefaultDecidesRedundancy()
    {
        // A subscription ending makes the plan layer silent for every feature, which is one of the
        // four triggers. POS ships on by default, so an override granting it is now redundant.
        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Pos, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool>(),
            platformDefaults: new Dictionary<Guid, bool> { [Pos] = true });

        Assert.Equal(Pos, Assert.Single(redundant));
    }

    [Fact]
    public void AnOverrideOnAFeatureWithNoKnownDefaultIsLeftAlone()
    {
        // An orphaned key (declared once, since removed from code) still has rows. Withdrawing an
        // exception whose baseline we cannot compute would be deleting data on a guess.
        Assert.Empty(GymFeatureExceptions.Redundant(
            [new GymFeatureExceptions.StoredException(Crm, IsEnabled: true)],
            planOpinions: new Dictionary<Guid, bool>(),
            platformDefaults: new Dictionary<Guid, bool>()));
    }

    [Fact]
    public void NoExceptionsIsNoWork()
    {
        Assert.Empty(GymFeatureExceptions.Redundant(
            [], new Dictionary<Guid, bool>(), new Dictionary<Guid, bool> { [Crm] = false }));
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    public void ItAgreesWithRequiresOverrideOnEveryInput(bool stored, bool? plan, bool @default)
    {
        // The rule must never fork. If this ever fails, one of the two has been edited alone.
        var defaults = new Dictionary<Guid, bool> { [Crm] = @default };
        var opinions = plan is { } value
            ? new Dictionary<Guid, bool> { [Crm] = value }
            : new Dictionary<Guid, bool>();

        bool treatedAsRedundant = GymFeatureExceptions
            .Redundant([new GymFeatureExceptions.StoredException(Crm, stored)], opinions, defaults)
            .Contains(Crm);

        Assert.Equal(!FeatureResolution.RequiresOverride(stored, plan, @default), treatedAsRedundant);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~GymFeatureExceptionsTests"
```

Expected: build error — the type does not exist.

- [ ] **Step 3: Write it**

```csharp
namespace GymOS.Platform.Domain.Features;

/// <summary>
/// Which of a gym's stored exceptions no longer differ from what it would get anyway.
///
/// <para>M4 enforced "an override exists only while it differs" at the instant of a write, and
/// nothing maintained it afterwards — so a gym upgraded onto a plan that grants what it had been
/// granted by exception kept a redundant row, reported <c>source: Override</c> for a capability it
/// now pays for, and on a later downgrade kept a capability it no longer buys. This is the function
/// that lets the invariant be maintained rather than merely asserted.</para>
///
/// <para><b>It does not define redundancy.</b> That is
/// <see cref="FeatureResolution.RequiresOverride"/> — the same function the write path calls. A
/// second definition would be exactly the drift this module already learned about the hard way.</para>
/// </summary>
public static class GymFeatureExceptions
{
    /// <summary>One override as stored: which feature, and what it forces.</summary>
    public sealed record StoredException(Guid FeatureId, bool IsEnabled);

    /// <param name="planOpinions">
    /// What the gym's live plan says, by feature. A feature absent from this map is one the plan is
    /// silent about — which is also every feature when the gym has no live subscription.
    /// </param>
    /// <param name="platformDefaults">
    /// The catalogue's defaults. A feature absent here has no computable baseline (an orphaned key),
    /// and its exception is left alone rather than withdrawn on a guess.
    /// </param>
    public static IReadOnlyList<Guid> Redundant(
        IEnumerable<StoredException> stored,
        IReadOnlyDictionary<Guid, bool> planOpinions,
        IReadOnlyDictionary<Guid, bool> platformDefaults)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(planOpinions);
        ArgumentNullException.ThrowIfNull(platformDefaults);

        var redundant = new List<Guid>();

        foreach (StoredException exception in stored)
        {
            if (!platformDefaults.TryGetValue(exception.FeatureId, out bool defaultEnabled))
            {
                continue;
            }

            bool? planEnabled = planOpinions.TryGetValue(exception.FeatureId, out bool opinion)
                ? opinion
                : null;

            if (!FeatureResolution.RequiresOverride(exception.IsEnabled, planEnabled, defaultEnabled))
            {
                redundant.Add(exception.FeatureId);
            }
        }

        return redundant;
    }
}
```

- [ ] **Step 4: Confirm green and commit**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests
```

Expected: 414 + 9 = **423 passed**, no `Aborted`.

```bash
git add src/Platform/GymOS.Platform.Domain/Features/GymFeatureExceptions.cs tests/GymOS.Platform.Domain.UnitTests/GymFeatureExceptionsTests.cs
git commit -m "M4.1 (1/7): which exceptions no longer differ — one rule, not a second one"
```

---

### Task 2: The synchronous hook, and the pinning test nobody wrote

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Features/Abstractions/IFeatureExceptionMaintainer.cs`, `src/Platform/GymOS.Platform.Application/Features/FeatureExceptionMaintainer.cs`
- Modify: `src/Platform/GymOS.Platform.Application/Subscriptions/SubscriptionCommands.cs` (assign + `MutateAsync`), `src/Platform/GymOS.Platform.Application/Subscriptions/SubscriptionRenewalService.cs`, the analytics/features DI extension
- Test: `tests/GymOS.Platform.Application.UnitTests/FeatureExceptionMaintainerTests.cs`, `tests/GymOS.IntegrationTests/GymFeatureLifecycleTests.cs`

**Interfaces:**
- Consumes: `GymFeatureExceptions.Redundant` (Task 1), `IFeatureRepository`, `IPlanRepository`, `ISubscriptionRepository`.
- Produces: `IFeatureExceptionMaintainer.WithdrawRedundantAsync(Guid gymId, CancellationToken)` → `Task<int>` (how many were withdrawn).

- [ ] **Step 1: Write the integration test first — it is the point of this task**

Create `tests/GymOS.IntegrationTests/GymFeatureLifecycleTests.cs`, mirroring `GymFeatureApiTests.cs`'s fixture:

```csharp
    [Fact]
    public async Task A_plan_upgrade_withdraws_an_exception_it_has_made_redundant()
    {
        // The test M4 never had. Every existing override test moves the override while the layers
        // beneath stand still, which is exactly why this defect survived six task reviews and a
        // single-pass review.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);

        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("upgrading-gym");
        await AssignAsync(gym, basic);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "3-month goodwill trial."));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // The upgrade. The plan now grants what the exception granted.
        await AssignAsync(gym, pro);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
        Assert.Null(crm.GetProperty("overrideReason").GetString());
    }

    [Fact]
    public async Task A_later_downgrade_does_not_resurrect_the_withdrawn_exception()
    {
        // The consequence that costs money: without withdrawal, the stale row wins on the way back
        // down and the gym keeps a capability it stopped paying for.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);
        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("round-trip-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "3-month goodwill trial."));
        await AssignAsync(gym, pro);
        await AssignAsync(gym, basic);

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.False(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_exception_that_still_differs_survives_a_plan_change()
    {
        // The guard against over-eager withdrawal: this one is still doing work.
        Guid basic = await CreatePlanAsync("BASIC");
        Guid other = await CreatePlanAsync("OTHER");
        Guid gym = await CreateGymAsync("still-differs-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Contractual."));

        await AssignAsync(gym, other);

        Assert.Equal(1, await CountOverridesAsync(gym));
        Assert.Equal("Override", Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm)
            .GetProperty("source").GetString());
    }
```

Copy `CountOverridesAsync`, `ReadFeaturesAsync`, `Feature`, `SetGymFeaturesAsync`, `SetPlanFeatureAsync`, `CreatePlanAsync`, `CreateGymAsync` and `AssignAsync` **by reference, not by duplication**: `GymFeatureApiTests.cs` already has all of them. If moving them to a shared helper is more than a few minutes, leave them and record it as a deferred minor — this task's value is the assertions, not the plumbing.

- [ ] **Step 2: Run it and watch it fail**

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~GymFeatureLifecycleTests"
```

Expected: the **first two** tests FAIL (the override survives the upgrade, so `CountOverridesAsync` is 1, and the source reads `Override`); the third passes already. That first failure is the Critical finding reproduced.

- [ ] **Step 3: Write the maintainer**

```csharp
// IFeatureExceptionMaintainer.cs
namespace GymOS.Platform.Application.Features.Abstractions;

/// <summary>
/// Withdraws a gym's exceptions that no longer differ from what it would get anyway.
///
/// <para>Called by every path that changes one gym's subscription, and by the nightly sweep for the
/// platform-wide triggers. Built as "re-evaluate this gym's exceptions" rather than "delete redundant
/// rows" because override <b>expiry</b>, when its product decision is taken, is one more predicate in
/// the same pass.</para>
/// </summary>
public interface IFeatureExceptionMaintainer
{
    /// <summary>
    /// Marks the redundant overrides for removal. <b>Does not save</b> — the caller's transaction
    /// does, so the withdrawal and the change that caused it commit together or not at all (the
    /// <c>InvoiceIssuer</c> precedent).
    /// </summary>
    Task<int> WithdrawRedundantAsync(Guid gymId, CancellationToken cancellationToken);
}
```

```csharp
internal sealed class FeatureExceptionMaintainer : IFeatureExceptionMaintainer
{
    private readonly IFeatureRepository _features;
    private readonly IPlanRepository _plans;
    private readonly ISubscriptionRepository _subscriptions;

    // constructor assigns the three

    public async Task<int> WithdrawRedundantAsync(Guid gymId, CancellationToken cancellationToken)
    {
        IReadOnlyList<GymFeatureOverride> stored =
            await _features.ListOverridesForGymAsync(gymId, cancellationToken);

        if (stored.Count == 0)
        {
            return 0;   // The common case: most gyms hold no exceptions at all.
        }

        IReadOnlyList<Feature> catalogue = await _features.ListAllAsync(cancellationToken);
        Dictionary<Guid, bool> defaults = catalogue.ToDictionary(
            feature => feature.Id, feature => feature.DefaultEnabled);

        Dictionary<Guid, bool> planOpinions = (await LoadPlanFeaturesAsync(gymId, cancellationToken))
            .ToDictionary(planFeature => planFeature.FeatureId, planFeature => planFeature.IsEnabled);

        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            stored.Select(over => new GymFeatureExceptions.StoredException(over.FeatureId, over.IsEnabled)),
            planOpinions,
            defaults);

        Dictionary<Guid, string> keys = catalogue.ToDictionary(
            feature => feature.Id, feature => feature.Key);

        foreach (Guid featureId in redundant)
        {
            GymFeatureOverride exception = stored.Single(over => over.FeatureId == featureId);

            // The same two calls the operator-driven clear path makes, so a withdrawal the system
            // decided on is indistinguishable in the audit trail from one an operator asked for —
            // which is correct: it IS the same decision, taken by the rule rather than by a person.
            exception.MarkCleared(keys[featureId]);
            _features.RemoveOverride(exception);
        }

        return redundant.Count;
    }
}
```

**`LoadPlanFeaturesAsync` already exists**, privately, on `SetGymFeaturesCommandHandler` — "the gym's live plan's feature opinions, or none when it has no live subscription", with a comment explaining that the read path and the write path must agree about it. **Extract it** so both callers share one copy rather than writing a second that can drift; that comment is the reason.

Note what this method does **not** do: call `SaveChangesAsync`. The caller's transaction commits the withdrawal together with the change that caused it.

- [ ] **Step 4: Call it from every gym-scoped trigger**

Three call sites, each **before** its `SaveChangesAsync` so the withdrawal joins the same transaction:

1. `AssignSubscriptionCommandHandler` — after the assignment/plan change is applied.
2. `SubscriptionStateCommandHandlers.MutateAsync` — covers cancel, resume, convert-trial, payment-state.
3. `SubscriptionRenewalService.ProcessAsync` — after `ConvertTrial`, `CancelNow` and `Expire`. **This one matters most**: a subscription that ends overnight is precisely the case nobody is watching, and the design says so.

- [ ] **Step 5: Pin the one contract integration tests cannot isolate**

Create `tests/GymOS.Platform.Application.UnitTests/FeatureExceptionMaintainerTests.cs` with a single purposeful test — that the maintainer **does not save**:

```csharp
    [Fact]
    public async Task ItMarksWithdrawalsButLeavesTheSaveToItsCaller()
    {
        // If it saved, the withdrawal would commit even when the subscription change that caused it
        // fails — leaving a gym stripped of an exception for a plan change that never happened.
        // Integration tests cannot see this: there, both halves commit either way.
        var context = new FeatureFakeContext();   // the suite's existing fakes
        context.GiveGymARedundantOverride();

        int withdrawn = await context.Maintainer.WithdrawRedundantAsync(context.GymId, default);

        Assert.Equal(1, withdrawn);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }
```

Match the fakes' actual shape in `tests/GymOS.Platform.Application.UnitTests/Fakes/FeatureFakes.cs` — the names above are this plan's guess, the file is the truth. Add a fake `IFeatureExceptionMaintainer` there too if the subscription handler tests need one.

- [ ] **Step 6: Register and verify**

Register in the features DI extension (`services.TryAddScoped<IFeatureExceptionMaintainer, FeatureExceptionMaintainer>()`), then:

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~GymFeatureLifecycleTests"
dotnet test tests/GymOS.Platform.Application.UnitTests
```

Expected: the three lifecycle tests pass; the application suite stays green.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "M4.1 (2/7): a subscription change withdraws the exceptions it makes redundant"
```

---

### Task 3: The nightly sweep

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Jobs/FeatureOverrideSweepJob.cs`
- Modify: the platform features DI extension (`AddScheduledJob<FeatureOverrideSweepJob>()`)
- Test: `tests/GymOS.IntegrationTests/FeatureOverrideSweepTests.cs`

**Interfaces:**
- Consumes: `IFeatureExceptionMaintainer` (Task 2), `IUnitOfWork`, `IDatabaseClock`, `IScheduledJob`.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task It_withdraws_an_exception_a_plan_edit_made_redundant()
    {
        // The platform-wide trigger: the operator edited the PLAN, not the gym, so nothing
        // synchronous ran. The sweep is the guarantee.
        Guid plan = await CreatePlanAsync("SWEEP");
        Guid gym = await CreateGymAsync("swept-gym");
        await AssignAsync(gym, plan);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));

        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);
        Assert.Equal(1, await CountOverridesAsync(gym));   // still there: no sweep yet

        await RunSweepAsync();

        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task It_withdraws_an_exception_a_default_flip_made_redundant()
    {
        Guid gym = await CreateGymAsync("default-swept-gym");   // no subscription: default decides
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));

        await SetFeatureDefaultAsync(PlatformFeatures.Crm, defaultEnabled: true);

        await RunSweepAsync();

        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task It_leaves_an_exception_that_still_differs()
    {
        Guid gym = await CreateGymAsync("untouched-gym");
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Contractual."));

        await RunSweepAsync();

        Assert.Equal(1, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task Running_it_twice_changes_nothing_the_second_time()
    {
        // The IScheduledJob contract: a re-run must be a no-op. A sweep that deleted something on
        // every pass would be deleting exceptions that still differ.
        Guid plan = await CreatePlanAsync("IDEM");
        Guid gym = await CreateGymAsync("idempotent-gym");
        await AssignAsync(gym, plan);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Goodwill."));
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);

        Assert.Equal(1, await RunSweepAsync());
        Assert.Equal(0, await RunSweepAsync());
    }
```

`RunSweepAsync()` resolves the job from a scope and calls `ExecuteAsync`, returning how many were withdrawn — mirror `RetentionJobTests.cs`'s way of driving a job by hand with `Jobs:Enabled=false`.

- [ ] **Step 2: Run and watch them fail**

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~FeatureOverrideSweepTests"
```

Expected: build error — the job does not exist.

- [ ] **Step 3: Write the job**

```csharp
internal sealed partial class FeatureOverrideSweepJob : IScheduledJob
{
    // constructor takes IServiceScopeFactory (a fresh scope per gym, the RenewalJob shape),
    // AppDbContext for the candidate query, and ILogger<FeatureOverrideSweepJob>

    public string Name => "feature-override-sweep";

    /// <summary>After RetentionJob (03:10) and PlatformStatsJob (03:30), so the night's work queues
    /// rather than contending.</summary>
    public JobSchedule Schedule => JobSchedule.DailyAtUtc(new TimeOnly(3, 50));

    public async Task<int> ExecuteAndCountAsync(CancellationToken cancellationToken)
    {
        // Candidates are the gyms that actually hold an exception — a small population by
        // construction, and the only ones a sweep could change.
        List<Guid> gyms = await _dbContext.Set<GymFeatureOverride>()
            .AsNoTracking()
            .Select(over => over.GymId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var withdrawn = 0;

        foreach (Guid gymId in gyms)
        {
            try
            {
                // One scope and one transaction per gym: a gym whose withdrawal fails must not
                // abandon the sweep for every gym after it (RenewalJob's reasoning).
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                var maintainer = scope.ServiceProvider.GetRequiredService<IFeatureExceptionMaintainer>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                int count = await maintainer.WithdrawRedundantAsync(gymId, cancellationToken);

                if (count > 0)
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    withdrawn += count;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogGymFailed(_logger, gymId, exception);
            }
        }

        LogSwept(_logger, gyms.Count, withdrawn);

        return withdrawn;
    }

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteAndCountAsync(cancellationToken);
}
```

`ExecuteAndCountAsync` exists so the tests can assert **how many** were withdrawn — which is what makes the idempotence test meaningful (1 on the first pass, 0 on the second). `IScheduledJob` only needs `ExecuteAsync`.

Cross-instance exclusion is `JobHost`'s, not the job's: do not take a lock. Logging is source-generated `[LoggerMessage]` partial methods, per the house rule.

- [ ] **Step 4: Verify and commit**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~FeatureOverrideSweepTests"
```

```bash
git add src tests
git commit -m "M4.1 (3/7): the nightly sweep for the triggers too wide to do inline"
```

---

### Task 4: The contract-shaping changes

**Files:**
- Modify: `src/Platform/GymOS.Platform.Application/Features/Abstractions/IFeatureQueries.cs`, `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/FeatureQueries.cs`, `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/GymQueries.cs`, `src/Host/GymOS.WebApi/Endpoints/GymEndpoints.cs`, `contracts/openapi-v1.json`
- Test: `tests/GymOS.IntegrationTests/GymFeatureApiTests.cs`, `tests/GymOS.IntegrationTests/FeatureCatalogueApiTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task The_gym_detail_omits_the_features_block_without_features_read()
    {
        // gyms.read alone must not expose overrideReason — the commercially sensitive free text the
        // module's own docs call "what a later billing conversation turns on". The sibling route
        // requires features.read at both layers; this one bypassed both.
        using HttpClient limited = await SignedInWithPermissionsAsync("gyms.read");

        using HttpResponseMessage detail = await limited.GetAsync(new Uri($"/api/v1/gyms/{_gym}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("features", out JsonElement features) &&
                     features.ValueKind is not JsonValueKind.Null);
    }

    [Fact]
    public async Task An_override_carries_who_set_it_and_when()
    {
        // The only compensating control while overrides never expire: "how old is this grant, and
        // who made it" without going to the audit log.
        await SetGymFeatureAsync(_gym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement crm = Feature(await ReadFeaturesAsync(_gym), PlatformFeatures.Crm);
        Assert.Equal(AuthenticationHost.SuperAdminEmail, crm.GetProperty("overrideSetByEmail").GetString());
        Assert.NotNull(crm.GetProperty("overrideSetAtUtc").GetString());
    }

    [Fact]
    public async Task A_feature_the_code_no_longer_declares_is_marked_undeclared()
    {
        // Insert a row for a key no module declares, exactly as a rename would leave behind.
        await InsertUndeclaredFeatureAsync("legacy_thing");

        JsonElement catalogue = await CatalogueAsync();
        Assert.False(FeatureByKey(catalogue, "legacy_thing").GetProperty("isDeclared").GetBoolean());
        Assert.True(FeatureByKey(catalogue, PlatformFeatures.Pos).GetProperty("isDeclared").GetBoolean());
    }
```

`SignedInWithPermissionsAsync` may not exist — the suite signs in **by role**. If no role holds `gyms.read` without `features.read`, create one through the roles API in the test, or assert the equivalent by other means; do **not** weaken the assertion to fit the helper.

- [ ] **Step 2: Run and watch them fail**

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~GymFeatureApiTests|FullyQualifiedName~FeatureCatalogueApiTests"
```

- [ ] **Step 3: Make the three changes**

1. `ResolvedFeatureDetail` gains `DateTimeOffset? OverrideSetAtUtc` and `string? OverrideSetByEmail`, populated in `ResolveForGymAsync`'s projection (join `users` for the email, as `GymFeatureApiTests.SetByEmailAsync` already does in SQL) and **null unless the override decided the answer** — the same rule `OverrideReason` follows.
2. `FeatureListItem` gains `bool IsDeclared`, computed against `PlatformFeatures.All`'s keys. Do it **in memory in `Map`**, not in SQL: the declared set is a code constant and pushing it into the query would put a second copy of the catalogue in the database layer.
3. `GetGymQuery`'s handler asks `ICurrentUser`/the permission evaluator whether the caller holds `features.read`, and `GymQueries.GetAsync` skips `ResolveForGymAsync` when it does not, leaving `features` null. Update `GymEndpoints`' description to say when the block is absent.

- [ ] **Step 4: Regenerate the contract and read the diff**

```
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~OpenApiContractTests"
git diff contracts/openapi-v1.json
```

Expected, semantically: three properties added, `GymDetail.features` becomes nullable, nothing removed. Record what it showed. Anything else means something unintended changed.

- [ ] **Step 5: Verify and commit**

```bash
git add -A
git commit -m "M4.1 (4/7): the gym detail stops leaking exceptions, and the console gets what it needs"
```

---

### Task 5: The write paths

**Files:**
- Modify: `src/Platform/GymOS.Platform.Application/Features/GymFeatureCommands.cs`, `src/Platform/GymOS.Platform.Application/Features/PlanFeatureCommands.cs`, `src/Host/GymOS.WebApi/Endpoints/PlanEndpoints.cs`
- Test: `tests/GymOS.IntegrationTests/PlanApiTests.cs`, `tests/GymOS.IntegrationTests/GymFeatureApiTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task An_empty_body_is_refused_not_a_server_error()
    {
        using HttpResponseMessage response = await PutFeaturesRawAsync(_plan, "{}");

        // Was a 500: Features bound to null and the handler dereferenced it. A caller mistake must
        // never be reported as a server fault, and the sibling gym route already guarded this.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_same_key_twice_is_refused_here_too()
    {
        using HttpResponseMessage response = await SetFeaturesAsync(
            _plan, [(PlatformFeatures.Crm, true), (PlatformFeatures.Crm, null)]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("features.duplicate_key", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task Two_identical_concurrent_grants_both_succeed()
    {
        // The documented promise is "the later decision wins, and the audit trail keeps the earlier
        // one". The loser used to get a 409 telling it to choose a value that is still free — advice
        // that cannot be followed, for a request that would succeed on retry.
        Guid gym = await CreateGymAsync("racing-gym");

        Task<HttpResponseMessage>[] both =
        [
            SetGymFeatureRawAsync(gym, PlatformFeatures.Crm, true, "First."),
            SetGymFeatureRawAsync(gym, PlatformFeatures.Crm, true, "Second."),
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(both);

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Equal(1, await CountOverridesAsync(gym));
    }
```

- [ ] **Step 2: Run and watch them fail**

- [ ] **Step 3: Fix the three paths**

1. `PlanEndpoints.SetFeaturesAsync` passes `request.Features ?? []`, matching `GymEndpoints`.
2. `SetPlanFeaturesCommandHandler` refuses a repeated key with `FeatureErrors.DuplicateKey`, using the same `seen`-set shape as `SetGymFeaturesCommandHandler.PlanChangesAsync` — the reasoning recorded there applies verbatim.
3. `SetGymFeaturesCommandHandler` catches `UniquenessConflictException` on `ux_gym_feature_overrides_gym_feature` and **retries the whole handler once**, which finds the row on the second pass and `Restate`s it. A second failure surfaces unchanged. Put the reason in a comment: a retry here is not papering over a race, it is what makes "the later decision wins" true.

- [ ] **Step 4: Close the plan-edit race**

In `SetGymFeaturesCommandHandler`, load the gym's live plan with a `FOR UPDATE` read inside the handler's transaction (`IPlanRepository.FindByIdForUpdateAsync` if it exists; add it on `SubscriptionRepository.FindByIdForUpdateAsync`'s pattern if not), so a concurrent `PUT /plans/{id}/features` serialises behind it rather than making the decision stale between reading and saving.

Add an integration test that runs a gym-feature write and a plan-feature write concurrently and asserts the gym's final resolution matches the response the gym write returned.

- [ ] **Step 5: Verify and commit**

```bash
git add -A
git commit -m "M4.1 (5/7): the write paths keep the promises their docs make"
```

---

### Task 6: The seeder race, the read-back cost, and the index that hardcodes its filter

**Files:**
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Seeding/FeatureCatalogSeeder.cs`, `.../PlatformIdentitySeeder.cs`, `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/FeatureQueries.cs`
- Test: `tests/GymOS.IntegrationTests/FeatureCatalogSeedingTests.cs`, `tests/GymOS.Platform.Infrastructure.UnitTests/` (or the integration suite, for the index assertion)

- [ ] **Step 1: Serialise the seeders**

Wrap both seeders' work in `IAdvisoryLock.AcquireAsync("platform.catalog-seed", ct)` inside a transaction. Two instances booting against a fresh database currently both insert and one dies on the unique index — and because `FeatureCatalogSeeder` calls `SaveChangesAsync` directly rather than through `IUnitOfWork`, the raw `DbUpdateException` escapes `SeedAsync`, reaches `Program.cs`'s catch-all, and terminates the host. `PlatformIdentitySeeder` has the identical exposure; fix both, because it is one pattern rather than two bugs.

- [ ] **Step 2: Stop the read-back paying for adoption counts**

`FeatureQueries.GetAsync` shares `Project()`, so `PUT /features/{id}` runs the full O(features × gyms) scan to return one row. Give it a count-free projection. The **list** endpoint keeps its counts — they are the reason it exists. Note in the code comment which one pays and why.

- [ ] **Step 3: Pin the index filter to the declared set**

`ux_gym_subscriptions_one_live_per_gym` hardcodes `status IN (1, 2, 3)`. Add a test reading `pg_indexes` and asserting the filter's values match `SubscriptionStatusTransitions.Live` cast to int — so if `Live` ever grows, the test fails instead of the index silently ceasing to guarantee the invariant that `FeatureQueries`' `FirstOrDefault` and `SubscriptionRepository`'s `SingleOrDefault` disagree about.

- [ ] **Step 4: Verify and commit**

```bash
git add -A
git commit -m "M4.1 (6/7): one seeder at a time, a cheaper read-back, and an index that cannot drift"
```

---

### Task 7: Documentation, the SQL/domain parity test, verification and the live run

**Files:**
- Modify: `docs/BusinessRules/Features.md`, `docs/STATUS.md`
- Test: `tests/GymOS.IntegrationTests/FeatureAdoptionParityTests.cs`

- [ ] **Step 1: Pin the SQL adoption count against the domain rule**

Build a matrix of gyms — no subscription, a plan that includes, a plan that excludes, a plan that is silent, an override each way, a deleted gym — then assert that `GET /features`' `adoptionCount` for each feature equals the count computed by calling `FeatureResolution.Resolve` per gym in C#. This is the test that stops the two implementations drifting when expiry adds a fourth input to `Resolve`.

- [ ] **Step 2: Correct the documentation**

In `Features.md` and `STATUS.md`:

- **Delete the claim that resolution is "never expressed in SQL."** Replace with: one authoritative definition in `FeatureResolution`, plus one deliberate SQL restatement for the adoption count, which exists because counting adoption per gym in C# would mean loading every gym — and which Step 1's test pins.
- **Reword the invariant** to the guarantee that is actually delivered: a redundant exception is withdrawn immediately when the change is to that gym, and within a day when it is to a plan or a platform default. Name the sweep and its schedule.
- Record the `Feature.Rename` → `Describe(name, description, category)` deviation from the M4 design.
- Update the deferred-minors list: what this increment fixed, and what remains (the four-subquery plan projection, tracked catalogue reads, the log-count conflation).
- Add the M4.1 entry to STATUS's milestone table and rewrite "Next step" to point at F3.

- [ ] **Step 3: Full verification**

```
dotnet build GymOS.Backend.slnx
```
0 warnings. Then every project individually, checking for `Aborted`. Baseline 1304 plus this plan's additions; count them rather than assuming.

- [ ] **Step 4: Live run**

```
docker compose up -d --build
```

Grant a gym an override on a plan that excludes the feature, then reassign it to a plan that includes the same feature, and read `GET /gyms/{id}/features`: the exception is gone and `source` reads `Plan`. That is the Critical finding demonstrated fixed. Then confirm `GET /gyms/{id}` still carries the block for a SuperAdmin. Record actual output, including anything that did not work.

- [ ] **Step 5: Commit and stop for review**

```bash
git add -A
git commit -m "M4.1 (7/7): the rules as they now are, verified and proved live"
```

Report: what was fixed, the verified test count, what the live run showed, and which review findings were deliberately left (§6 of the design) so a reviewer can accept or reject that triage.
