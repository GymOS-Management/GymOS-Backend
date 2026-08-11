# M5 — Dashboard & Analytics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Super Admin console a live dashboard summary and three analytics series, backed by a daily read model that a background job writes once per day.

**Architecture:** The dashboard reads **live** (counts and MRR computed from `gyms` and `gym_subscriptions` on request); the analytics endpoints read **only** `platform.daily_platform_stats`, which a nightly job writes once per day and never recomputes. One pure domain calculator, `MonthlyRecurringRevenue`, is used by both paths so the platform's headline number has a single definition.

**Tech Stack:** .NET 10, EF Core + PostgreSQL, `IScheduledJob`/`JobHost` (M3.4), `Money`/`Currency` (AD-42), minimal APIs with `Asp.Versioning`, xUnit + Testcontainers.

**Design:** `docs/superpowers/specs/2026-08-11-m5-dashboard-analytics-design.md` (approved 2026-08-11).

## Global Constraints

- **Architecture is frozen.** Deviations need a superseding ADR in `GymOS-Specification/ADR-CHANGELOG.md` *first*. The two deviations M5 makes are already recorded in the design (§4 per-currency MRR, §6 split churn) — do not add more.
- **Increment protocol:** one commit per task. Build + tests green before moving on. **Stop for review at the end of Task 6**, not before.
- **`IClock` only** — BCL clocks (`DateTime.UtcNow`, `DateTimeOffset.Now`) are analyzer-banned. Time flows inward as a parameter (AD-40).
- **R4:** a `decimal` member named like money outside `Money` fails the build. Monetary values are `Money`.
- **Business failures are `Result`; invariant breaches are `DomainException`.** Events are raised in aggregates only.
- **Logging is source-generated `LoggerMessage`** (`[LoggerMessage]` partial methods), never interpolated calls.
- **Migrations are never hand-edited** beyond adding documented raw SQL; the generated-code `.editorconfig` applies.
- **`TreatWarningsAsErrors` is on.** A build with warnings is a failed build.
- **Test runs must be complete.** A run is only trustworthy if its output contains no `Aborted` and the totals match. Baseline before this plan: **1267** (126 SharedKernel · 62 BuildingBlocks · 151 Architecture · 394 Domain · 232 Application · 67 Infrastructure · 235 Integration).
- **Docker must be running** for any integration test (Testcontainers) and for the live runs.

---

### Task 0: Extract the shared integration-test helpers

**Added after the pre-flight scan, by the owner's ruling.** Tasks 3–5 would otherwise copy test helpers verbatim, which the review rubric treats as a defect. A census found the duplication is already wide:

| Helper | Copies today |
|---|---|
| `SignedInAsync`, `RoleIdAsync`, `FindUserIdAsync`, `SetRolesAsync` | **7 files** |
| `CreateGymAsync`, `CreatePlanAsync` | **4 files** |
| `AssignAsync` | **3 files** |

Extracting while leaving the existing copies in place would make it eight copies, so this task **migrates every caller**. This is a pure refactor: no test's behaviour, name, or assertion changes, and the suite total stays exactly **1267**.

**Files:**
- Create: `tests/GymOS.IntegrationTests/PlatformApiHelpers.cs`
- Modify: `AuditCaptureTests.cs`, `FeatureCatalogueApiTests.cs`, `GymApiTests.cs`, `GymFeatureApiTests.cs`, `InvoiceApiTests.cs`, `PlanApiTests.cs`, `PlatformAdminApiTests.cs`, `SubscriptionApiTests.cs` (all in `tests/GymOS.IntegrationTests/`)

**Interfaces:**
- Consumes: `AuthenticationHost`, `AuthApi` (existing test infrastructure).
- Produces: `PlatformApiHelpers` — the shared surface Tasks 3, 4 and 5 use instead of copying. **You choose the exact signatures**; they must cover every existing caller's needs without a caller passing a value it does not care about. Record the final signatures in your report file, because later tasks are dispatched against them.

- [ ] **Step 1: Read every caller before designing the surface**

Read all seven files' copies of each helper. **They are not identical** — `CreatePlanAsync` differs in plan code, price and trial days between files, and some callers need the created plan's id while others need its row version. The shared helper must serve every existing call site without forcing a caller to invent an argument it does not care about; default parameters are the natural tool.

Do **not** design the surface from this plan's description alone. The variations are the whole difficulty of this task.

- [ ] **Step 2: Establish the baseline**

```
dotnet test tests/GymOS.IntegrationTests
```

Expected: **235 passed**, no `Aborted`. Write the number down; it is the thing this task must not change.

- [ ] **Step 3: Write `PlatformApiHelpers`**

A `static class` (or an instance type taking the host, if that serves the call sites better) in `tests/GymOS.IntegrationTests/`, with XML doc comments explaining what each helper does — these are read by every future test author. Keep the helpers thin: they are HTTP calls with `Assert.Equal(HttpStatusCode.…, response.StatusCode)` on the result, not a second test framework.

- [ ] **Step 4: Migrate the callers one file at a time**

After **each** file, run that file's tests:

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~<TestClassName>"
```

One file at a time, because a broken shared helper otherwise fails seven suites at once and the cause is no longer obvious.

- [ ] **Step 5: Confirm nothing moved**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests
```

Expected: **0 warnings**, **235 passed**, no `Aborted` — the identical number from Step 2. A different total means a test was lost or renamed, which this task must not do.

- [ ] **Step 6: Commit**

```bash
git add tests/GymOS.IntegrationTests
git commit -m "test: one copy of the integration helpers, not seven"
```

---

### Task 1: The MRR calculator and the `Billing` status set

Pure domain arithmetic, no database. This lands first because both the job (Task 3) and the live dashboard (Task 4) depend on it, and it is the piece most worth getting exhaustively tested while it has no dependencies.

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Analytics/MonthlyRecurringRevenue.cs`
- Modify: `src/Platform/GymOS.Platform.Domain/Subscriptions/SubscriptionStatus.cs` (add `Billing` to `SubscriptionStatusTransitions`)
- Test: `tests/GymOS.Platform.Domain.UnitTests/MonthlyRecurringRevenueTests.cs`

**Interfaces:**
- Consumes: `Money`, `Currency`, `RoundingPolicy` (SharedKernel); `SubscriptionStatus`, `BillingCycle`.
- Produces:
  - `SubscriptionStatusTransitions.Billing` → `SubscriptionStatus[]`
  - `SubscriptionStatusTransitions.IsBilling(SubscriptionStatus)` → `bool`
  - `record MrrContribution(SubscriptionStatus Status, BillingCycle Cycle, Money Price)`
  - `MonthlyRecurringRevenue.Calculate(IEnumerable<MrrContribution>)` → `IReadOnlyList<Money>` (one entry per currency, ordered by currency code)

- [ ] **Step 1: Write the failing test**

Create `tests/GymOS.Platform.Domain.UnitTests/MonthlyRecurringRevenueTests.cs`:

```csharp
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class MonthlyRecurringRevenueTests
{
    private static readonly Currency Eur = Currency.Of("EUR");
    private static readonly Currency Gbp = Currency.Of("GBP");

    private static MrrContribution Sub(
        SubscriptionStatus status, BillingCycle cycle, decimal amount, Currency? currency = null) =>
        new(status, cycle, Money.Of(amount, currency ?? Eur));

    [Fact]
    public void AMonthlySubscriptionContributesItsPrice()
    {
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [Sub(SubscriptionStatus.Active, BillingCycle.Monthly, 49.99m)]);

        Assert.Equal(Money.Of(49.99m, Eur), Assert.Single(mrr));
    }

    [Theory]
    [InlineData(BillingCycle.Quarterly, 300, 100)]
    [InlineData(BillingCycle.Yearly, 480, 40)]
    public void ALongerCycleIsNormalisedToOneMonth(BillingCycle cycle, decimal price, decimal expected)
    {
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [Sub(SubscriptionStatus.Active, cycle, price)]);

        Assert.Equal(Money.Of(expected, Eur), Assert.Single(mrr));
    }

    [Fact]
    public void PastDueCountsBecauseTheMoneyIsStillOwedAndServiceStillRuns()
    {
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [Sub(SubscriptionStatus.PastDue, BillingCycle.Monthly, 20m)]);

        Assert.Equal(Money.Of(20m, Eur), Assert.Single(mrr));
    }

    [Fact]
    public void ATrialContributesNothingBecauseItHasNeverPaid()
    {
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [Sub(SubscriptionStatus.Trialing, BillingCycle.Monthly, 99m)]);

        Assert.Empty(mrr);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Expired)]
    public void AnEndedSubscriptionContributesNothing(SubscriptionStatus status)
    {
        Assert.Empty(MonthlyRecurringRevenue.Calculate([Sub(status, BillingCycle.Monthly, 99m)]));
    }

    [Fact]
    public void UnlikeCurrenciesAreReportedSideBySideAndNeverSummed()
    {
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
        [
            Sub(SubscriptionStatus.Active, BillingCycle.Monthly, 100m, Eur),
            Sub(SubscriptionStatus.Active, BillingCycle.Monthly, 50m, Gbp),
            Sub(SubscriptionStatus.Active, BillingCycle.Monthly, 25m, Eur),
        ]);

        // Ordered by code so the API's output is stable, and never added together: adding EUR to
        // GBP is the silent coercion Money exists to stop, and the rate to do it honestly is an
        // accounting decision nobody has taken (AD-43).
        Assert.Collection(
            mrr,
            eur => Assert.Equal(Money.Of(125m, Eur), eur),
            gbp => Assert.Equal(Money.Of(50m, Gbp), gbp));
    }

    [Fact]
    public void AZeroDecimalCurrencyRoundsToWholeUnits()
    {
        // JPY has no minor units. A normalisation that assumed hundredths would produce an amount
        // Money itself refuses to hold, and this is the currency that catches it.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly,
                Money.Of(12_000m, Currency.Of("JPY")))]);

        Assert.Equal(Money.Of(1_000m, Currency.Of("JPY")), Assert.Single(mrr));
    }

    [Fact]
    public void NothingLiveIsAnEmptyResultRatherThanAZero()
    {
        // A currency-less zero has nowhere to live: Money.Zero needs a currency, and inventing one
        // would report "EUR 0.00" for a platform that has never sold in euros.
        Assert.Empty(MonthlyRecurringRevenue.Calculate([]));
    }

    [Fact]
    public void TheBillingSetIsLiveMinusTrials()
    {
        Assert.Equal(
            [SubscriptionStatus.Active, SubscriptionStatus.PastDue],
            SubscriptionStatusTransitions.Billing);

        Assert.True(SubscriptionStatusTransitions.IsBilling(SubscriptionStatus.PastDue));
        Assert.False(SubscriptionStatusTransitions.IsBilling(SubscriptionStatus.Trialing));

        // Live and Billing answer different questions and must not be confused: Live is
        // "does the one-per-gym rule apply", Billing is "does this contribute revenue".
        Assert.Contains(SubscriptionStatus.Trialing, SubscriptionStatusTransitions.Live);
    }
}
```

- [ ] **Step 2: Run the test and watch it fail**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~MonthlyRecurringRevenueTests"
```

Expected: **build error** — `MonthlyRecurringRevenue` and `SubscriptionStatusTransitions.Billing` do not exist. That is the failure; do not proceed until you see it.

- [ ] **Step 3: Add the `Billing` set**

In `src/Platform/GymOS.Platform.Domain/Subscriptions/SubscriptionStatus.cs`, inside `SubscriptionStatusTransitions`, directly beneath the existing `Live` array:

```csharp
    /// <summary>
    /// Statuses that contribute revenue — <see cref="Live"/> minus trials.
    ///
    /// <para>Deliberately its own set rather than <c>Live.Except(Trialing)</c> at the call site.
    /// The two answer different questions: <see cref="Live"/> is "does the one-live-subscription
    /// rule apply to this row", <c>Billing</c> is "does this money count". Writing the second as an
    /// adjustment of the first is how they end up conflated the next time one of them changes.</para>
    ///
    /// <para>PastDue is here because the money is genuinely owed and service is still running
    /// (M3.4: billing state never withholds service). Dropping it would turn a card that failed on
    /// Tuesday into a revenue dip that reverses itself on Thursday.</para>
    /// </summary>
    public static readonly SubscriptionStatus[] Billing =
    [
        SubscriptionStatus.Active,
        SubscriptionStatus.PastDue,
    ];

    public static bool IsBilling(SubscriptionStatus status) => Billing.Contains(status);
```

- [ ] **Step 4: Write the calculator**

Create `src/Platform/GymOS.Platform.Domain/Analytics/MonthlyRecurringRevenue.cs`:

```csharp
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Domain.Analytics;

/// <summary>One live subscription's input to the MRR sum — its status, its cycle, and what it pays.</summary>
public sealed record MrrContribution(SubscriptionStatus Status, BillingCycle Cycle, Money Price);

/// <summary>
/// Monthly recurring revenue: the sum, <b>per currency</b>, of every billing subscription's price
/// normalised to one month.
///
/// <para>A pure, total function, for the reason <c>FeatureResolution</c> is one: this is the
/// platform's headline number, it is read by both the live dashboard and the nightly stats job, and
/// two implementations of one definition eventually disagree. Keeping it as arithmetic over values
/// means it is exhaustively testable without a database and there is only one of it.</para>
///
/// <para><b>Currencies are never summed.</b> Adding EUR to GBP needs an exchange rate, and which
/// rate — spot at capture, or month-average — is an accounting decision nobody has taken (AD-43).
/// So the result is one <see cref="Money"/> per currency, and the day AD-43 lands this collapses to
/// one number without the definition changing.</para>
/// </summary>
public static class MonthlyRecurringRevenue
{
    public static IReadOnlyList<Money> Calculate(IEnumerable<MrrContribution> contributions)
    {
        ArgumentNullException.ThrowIfNull(contributions);

        return contributions
            .Where(contribution => SubscriptionStatusTransitions.IsBilling(contribution.Status))
            .GroupBy(contribution => contribution.Price.Currency.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Aggregate(
                Money.Zero(group.First().Price.Currency),
                (running, contribution) => running.Add(PerMonth(contribution))))
            .ToList();
    }

    /// <summary>
    /// The price of one month of this subscription. <see cref="BillingCycle"/>'s values are month
    /// counts, so the divisor is the enum value itself (M3.1).
    ///
    /// <para>Rounded half-even to the currency's minor units, and stated here rather than left
    /// implicit: twelve monthly figures need not re-sum to the annual price. MRR is an indicator,
    /// not an invoice — an invoice would use <see cref="Money.Allocate"/>, which conserves every
    /// cent, and there is nothing here to conserve it into.</para>
    /// </summary>
    private static Money PerMonth(MrrContribution contribution)
    {
        var months = (int)contribution.Cycle;

        return months == 1
            ? contribution.Price
            : contribution.Price.Multiply(1m / months, RoundingPolicy.HalfEven);
    }
}
```

- [ ] **Step 5: Run the tests and confirm they pass**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~MonthlyRecurringRevenueTests"
```

Expected: **PASS**, 10 tests. If `Money.Of` rejects `1m / 12` precision, the failure will be in `ALongerCycleIsNormalisedToOneMonth` — `Multiply` rounds, so this should not happen; if it does, do not change the test's expected values, fix the rounding call.

- [ ] **Step 6: Run the whole domain project and commit**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests
```

Expected: 394 + 10 = **404 passed**, no `Aborted`.

```bash
git add src/Platform/GymOS.Platform.Domain/Analytics/MonthlyRecurringRevenue.cs \
        src/Platform/GymOS.Platform.Domain/Subscriptions/SubscriptionStatus.cs \
        tests/GymOS.Platform.Domain.UnitTests/MonthlyRecurringRevenueTests.cs
git commit -m "M5 (1/6): MRR as one definition, and the Billing status set"
```

---

### Task 2: The read model — entity, mapping, migration

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Analytics/DailyPlatformStats.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/DailyPlatformStatsConfiguration.cs`
- Create: `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IPlatformStatsRepository.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Repositories/PlatformStatsRepository.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformAnalyticsServiceCollectionExtensions.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs` (add `builder.Services.AddPlatformAnalytics();` beside `AddPlatformFeatures()`)
- Generated: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations/*_AddPlatformStats.cs`
- Test: `tests/GymOS.Platform.Domain.UnitTests/DailyPlatformStatsTests.cs`
- Test: `tests/GymOS.IntegrationTests/PlatformStatsPersistenceTests.cs`

**Interfaces:**
- Consumes: Task 1's `MonthlyRecurringRevenue` is **not** used here; this task only stores results.
- Produces:
  - `DailyPlatformStats.Record(DateOnly statDate, GymCounts counts, IReadOnlyList<Money> mrr, DateTimeOffset now)` → `DailyPlatformStats`
  - `record GymCounts(int Total, int Active, int Suspended, int Provisioning, int New, int Canceled, int Expired)`
  - `DailyPlatformStats.StatDate`, `.Mrr` (`IReadOnlyCollection<PlatformMrr>`), and the count properties
  - `PlatformMrr.Amount` → `Money`
  - `IPlatformStatsRepository.ExistsForAsync(DateOnly, CancellationToken)` → `Task<bool>`; `.Add(DailyPlatformStats)`

- [ ] **Step 1: Write the failing domain test**

Create `tests/GymOS.Platform.Domain.UnitTests/DailyPlatformStatsTests.cs`:

```csharp
using GymOS.Platform.Domain.Analytics;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class DailyPlatformStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Yesterday = new(2026, 8, 10);
    private static readonly GymCounts Counts = new(Total: 5, Active: 3, Suspended: 1, Provisioning: 1, New: 2, Canceled: 1, Expired: 0);

    [Fact]
    public void RecordKeepsTheCountsAndTheDate()
    {
        DailyPlatformStats stats = DailyPlatformStats.Record(Yesterday, Counts, [], Now);

        Assert.Equal(Yesterday, stats.StatDate);
        Assert.Equal(5, stats.TotalGyms);
        Assert.Equal(3, stats.ActiveGyms);
        Assert.Equal(1, stats.SuspendedGyms);
        Assert.Equal(1, stats.ProvisioningGyms);
        Assert.Equal(2, stats.NewGyms);
        Assert.Equal(1, stats.CanceledGyms);
        Assert.Equal(0, stats.ExpiredGyms);
        Assert.Empty(stats.Mrr);
    }

    [Fact]
    public void RecordCarriesOneMrrRowPerCurrency()
    {
        DailyPlatformStats stats = DailyPlatformStats.Record(
            Yesterday, Counts, [Money.Of(120m, Currency.Of("EUR")), Money.Of(80m, Currency.Of("GBP"))], Now);

        Assert.Collection(
            stats.Mrr,
            eur => Assert.Equal(Money.Of(120m, Currency.Of("EUR")), eur.Amount),
            gbp => Assert.Equal(Money.Of(80m, Currency.Of("GBP")), gbp.Amount));
    }

    [Fact]
    public void TheSameCurrencyTwiceIsADefect()
    {
        // The calculator groups by currency, so two rows for one currency means the caller summed
        // wrongly or built the list by hand. Refusing here stops a chart double-counting a day.
        Assert.Throws<DomainException>(() => DailyPlatformStats.Record(
            Yesterday, Counts, [Money.Of(1m, Currency.Of("EUR")), Money.Of(2m, Currency.Of("EUR"))], Now));
    }

    [Fact]
    public void CountsCannotBeNegative()
    {
        Assert.Throws<DomainException>(() => DailyPlatformStats.Record(
            Yesterday, Counts with { Total = -1 }, [], Now));
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~DailyPlatformStatsTests"
```

Expected: build error — the type does not exist.

- [ ] **Step 3: Write the entity**

Create `src/Platform/GymOS.Platform.Domain/Analytics/DailyPlatformStats.cs`:

```csharp
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Analytics;

/// <summary>The gym counts of one day, passed as a group so a caller cannot get their order wrong.</summary>
public sealed record GymCounts(
    int Total, int Active, int Suspended, int Provisioning, int New, int Canceled, int Expired);

/// <summary>One currency's MRR on one day. Part of the <see cref="DailyPlatformStats"/> aggregate.</summary>
public sealed class PlatformMrr : Entity
{
    internal PlatformMrr(Guid id, Guid statsId, Money amount)
        : base(id)
    {
        StatsId = statsId;
        Amount = amount;
    }

    private PlatformMrr()
    {
    }

    public Guid StatsId { get; private set; }

    public Money Amount { get; private set; }
}

/// <summary>
/// What was true about the platform on one day (ARCHITECTURE.md §2.4's <c>daily_platform_stats</c>,
/// AD-8) — the only source the analytics endpoints read.
///
/// <para><b>Written once, never recomputed.</b> Status counts are point-in-time facts and nothing in
/// the schema records what a gym's status was last Tuesday, so a "recompute" would write today's
/// answer under an old date and call it history. The job inserts when the day is absent and does
/// nothing when it is present; a missed night stays a gap, which is honest, rather than a row that
/// is half-real with nothing on it saying which half.</para>
///
/// <para>Not audited and carrying no concurrency token, unlike every aggregate that holds a
/// decision: this is derived data with exactly one writer, which <c>IJobLock</c> already serialises.
/// The audit trail of a computed row is the job's log.</para>
/// </summary>
public sealed class DailyPlatformStats : AggregateRoot
{
    private readonly List<PlatformMrr> _mrr = [];

    private DailyPlatformStats(Guid id, DateOnly statDate)
        : base(id) => StatDate = statDate;

    private DailyPlatformStats()
    {
    }

    public DateOnly StatDate { get; private set; }

    public int TotalGyms { get; private set; }

    public int ActiveGyms { get; private set; }

    public int SuspendedGyms { get; private set; }

    public int ProvisioningGyms { get; private set; }

    public int NewGyms { get; private set; }

    public int CanceledGyms { get; private set; }

    public int ExpiredGyms { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>One row per currency — see <c>MonthlyRecurringRevenue</c> on why they are not summed.</summary>
    public IReadOnlyCollection<PlatformMrr> Mrr => _mrr.AsReadOnly();

    public static DailyPlatformStats Record(
        DateOnly statDate,
        GymCounts counts,
        IReadOnlyList<Money> mrr,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(mrr);

        if (counts.Total < 0 || counts.Active < 0 || counts.Suspended < 0 || counts.Provisioning < 0
            || counts.New < 0 || counts.Canceled < 0 || counts.Expired < 0)
        {
            throw new DomainException("A platform stat count cannot be negative.");
        }

        // Two rows for one currency would double-count that day on every chart that reads it, and
        // the only way to produce one is a caller that summed wrongly — a defect, not a bad request.
        if (mrr.Select(amount => amount.Currency.Code).Distinct(StringComparer.Ordinal).Count() != mrr.Count)
        {
            throw new DomainException("MRR carries one amount per currency; a currency appeared twice.");
        }

        var stats = new DailyPlatformStats(Guid.NewGuid(), statDate)
        {
            TotalGyms = counts.Total,
            ActiveGyms = counts.Active,
            SuspendedGyms = counts.Suspended,
            ProvisioningGyms = counts.Provisioning,
            NewGyms = counts.New,
            CanceledGyms = counts.Canceled,
            ExpiredGyms = counts.Expired,
            CreatedAtUtc = now,
        };

        foreach (Money amount in mrr)
        {
            stats._mrr.Add(new PlatformMrr(Guid.NewGuid(), stats.Id, amount));
        }

        return stats;
    }
}
```

- [ ] **Step 4: Run the domain test — it must pass**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~DailyPlatformStatsTests"
```

Expected: **PASS**, 4 tests.

- [ ] **Step 5: Map it**

Create `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/DailyPlatformStatsConfiguration.cs`:

```csharp
using GymOS.Platform.Domain.Analytics;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class DailyPlatformStatsConfiguration : IEntityTypeConfiguration<DailyPlatformStats>
{
    public void Configure(EntityTypeBuilder<DailyPlatformStats> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("daily_platform_stats", "platform");
        builder.HasKey(stats => stats.Id);

        // One row per day, and the write-once rule's last line of defence: a second insert for a
        // date already recorded is refused by the database, not merely skipped by the job.
        builder.HasIndex(stats => stats.StatDate)
            .IsUnique()
            .HasDatabaseName("ux_daily_platform_stats_date");

        builder.Metadata
            .FindNavigation(nameof(DailyPlatformStats.Mrr))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(stats => stats.Mrr)
            .WithOne()
            .HasForeignKey(mrr => mrr.StatsId)
            .OnDelete(DeleteBehavior.Cascade);

        // No xmin: one writer, serialised by IJobLock, and rows are never updated. See the aggregate.
    }
}

public sealed class PlatformMrrConfiguration : IEntityTypeConfiguration<PlatformMrr>
{
    public void Configure(EntityTypeBuilder<PlatformMrr> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("daily_platform_stats_mrr", "platform");
        builder.HasKey(mrr => mrr.Id);

        builder.HasMoney(mrr => mrr.Amount, "amount");

        // The (stats, currency) uniqueness lives in the migration as raw SQL: EF cannot index into a
        // complex property ("no corresponding CLR property or field"), the same limitation
        // AddSubscriptionPlans documented for (plan, cycle, currency). The aggregate refuses a
        // duplicate currency in code, so a caller gets a typed error; the index holds under a bug.
    }
}
```

- [ ] **Step 6: Generate the migration**

Ensure PostgreSQL is up (`docker compose up -d postgres`), then:

```
dotnet ef migrations add AddPlatformStats \
  --project src/Platform/GymOS.Platform.Infrastructure \
  --startup-project src/Host/GymOS.WebApi
```

Open the generated `*_AddPlatformStats.cs` and **append** the raw-SQL unique index at the end of `Up`, with its comment (this is the one permitted hand-edit — a documented raw-SQL addition):

```csharp
            // EF cannot build an index across a complex property's member, so the (stats, currency)
            // uniqueness is expressed here — the AddSubscriptionPlans precedent for prices.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ux_daily_platform_stats_mrr_currency
                ON platform.daily_platform_stats_mrr (stats_id, amount_currency);
                """);
```

and the matching drop at the start of `Down`:

```csharp
            migrationBuilder.Sql("DROP INDEX IF EXISTS platform.ux_daily_platform_stats_mrr_currency;");
```

- [ ] **Step 7: Write the repository and its registration**

Create `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IPlatformStatsRepository.cs`:

```csharp
using GymOS.Platform.Domain.Analytics;

namespace GymOS.Platform.Application.Analytics.Abstractions;

/// <summary>Write access to the daily read model. There is no update: rows are written once.</summary>
public interface IPlatformStatsRepository
{
    Task<bool> ExistsForAsync(DateOnly statDate, CancellationToken cancellationToken);

    void Add(DailyPlatformStats stats);
}
```

Create `src/Platform/GymOS.Platform.Infrastructure/Persistence/Repositories/PlatformStatsRepository.cs`:

```csharp
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

internal sealed class PlatformStatsRepository : IPlatformStatsRepository
{
    private readonly AppDbContext _dbContext;

    public PlatformStatsRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<bool> ExistsForAsync(DateOnly statDate, CancellationToken cancellationToken) =>
        _dbContext.Set<DailyPlatformStats>().AnyAsync(stats => stats.StatDate == statDate, cancellationToken);

    public void Add(DailyPlatformStats stats) => _dbContext.Set<DailyPlatformStats>().Add(stats);
}
```

Create `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformAnalyticsServiceCollectionExtensions.cs`:

```csharp
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>Composes the M5 dashboard and analytics adapters. One list per surface, like the others.</summary>
public static class PlatformAnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPlatformStatsRepository, PlatformStatsRepository>();

        return services;
    }
}
```

In `src/Host/GymOS.WebApi/Program.cs`, directly after `builder.Services.AddPlatformFeatures();`:

```csharp
    builder.Services.AddPlatformAnalytics();
```

- [ ] **Step 8: Write the persistence round-trip test**

Create `tests/GymOS.IntegrationTests/PlatformStatsPersistenceTests.cs`. Follow the existing pattern in `FeatureCatalogSeedingTests.cs` for fixture setup — read that file first and mirror its `IAsyncLifetime` + `PostgresFixture.CreateIsolatedDatabaseAsync` shape exactly. The test body:

```csharp
    [Fact]
    public async Task AStatRowRoundTripsWithItsPerCurrencyMrr()
    {
        await using (var write = _host.CreateDbContext())
        {
            write.Add(DailyPlatformStats.Record(
                new DateOnly(2026, 8, 10),
                new GymCounts(5, 3, 1, 1, 2, 1, 0),
                [Money.Of(120.50m, Currency.Of("EUR")), Money.Of(80m, Currency.Of("GBP"))],
                new DateTimeOffset(2026, 8, 11, 3, 30, 0, TimeSpan.Zero)));
            await write.SaveChangesAsync();
        }

        await using var read = _host.CreateDbContext();
        DailyPlatformStats stored = await read.Set<DailyPlatformStats>()
            .Include(stats => stats.Mrr)
            .SingleAsync(stats => stats.StatDate == new DateOnly(2026, 8, 10));

        Assert.Equal(3, stored.ActiveGyms);
        Assert.Equal(
            [Money.Of(80m, Currency.Of("GBP")), Money.Of(120.50m, Currency.Of("EUR"))],
            stored.Mrr.Select(mrr => mrr.Amount).OrderBy(amount => amount.Currency.Code).ToList());
    }

    [Fact]
    public async Task TheSameDayCannotBeRecordedTwice()
    {
        // The write-once rule's guarantee: the job skips a present day, and if a bug ever gets past
        // that check the database refuses it rather than growing a second version of history.
        await using var context = _host.CreateDbContext();
        context.Add(DailyPlatformStats.Record(new DateOnly(2026, 8, 9), new GymCounts(1, 1, 0, 0, 0, 0, 0), [], DateTimeOffset.UnixEpoch));
        await context.SaveChangesAsync();

        context.Add(DailyPlatformStats.Record(new DateOnly(2026, 8, 9), new GymCounts(2, 2, 0, 0, 0, 0, 0), [], DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());
    }
```

- [ ] **Step 9: Run the integration tests**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~PlatformStatsPersistenceTests"
```

Expected: **PASS**, 2 tests. If the migration was not applied, the failure is `relation "platform.daily_platform_stats" does not exist` — the test host migrates on start, so that means the migration file was not generated into the Infrastructure project.

- [ ] **Step 10: Commit**

```bash
git add src/Platform/GymOS.Platform.Domain/Analytics src/Platform/GymOS.Platform.Application/Analytics \
        src/Platform/GymOS.Platform.Infrastructure src/Host/GymOS.WebApi/Program.cs \
        tests/GymOS.Platform.Domain.UnitTests/DailyPlatformStatsTests.cs \
        tests/GymOS.IntegrationTests/PlatformStatsPersistenceTests.cs
git commit -m "M5 (2/6): the daily read model — written once, one MRR row per currency"
```

---

### Task 3: `PlatformStatsJob` — compile yesterday, once

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Analytics/DailyStatsCompiler.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Jobs/PlatformStatsJob.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformAnalyticsServiceCollectionExtensions.cs` (register the compiler + `AddScheduledJob<PlatformStatsJob>()`)
- Test: `tests/GymOS.IntegrationTests/PlatformStatsJobTests.cs`

**Interfaces:**
- Consumes: `MonthlyRecurringRevenue.Calculate`, `MrrContribution`, `GymCounts`, `DailyPlatformStats.Record`, `IPlatformStatsRepository`, `IDatabaseClock`, `IUnitOfWork`.
- Produces: `DailyStatsCompiler.CompileAsync(DateOnly statDate, DateTimeOffset now, CancellationToken)` → `Task<bool>` (`true` when a row was written, `false` when the day was already recorded).

- [ ] **Step 1: Write the compiler**

Create `src/Platform/GymOS.Platform.Infrastructure/Analytics/DailyStatsCompiler.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Analytics;

/// <summary>
/// Turns one day into one <see cref="DailyPlatformStats"/> row, or does nothing because the day is
/// already recorded.
///
/// <para>Separate from the job so the decision is testable without a scheduler, and so the same
/// compilation can be triggered by hand when a gap needs filling deliberately.</para>
/// </summary>
internal sealed class DailyStatsCompiler
{
    private readonly AppDbContext _dbContext;
    private readonly IPlatformStatsRepository _stats;
    private readonly IUnitOfWork _unitOfWork;

    public DailyStatsCompiler(AppDbContext dbContext, IPlatformStatsRepository stats, IUnitOfWork unitOfWork)
    {
        _dbContext = dbContext;
        _stats = stats;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> CompileAsync(DateOnly statDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Write-once: the row is a point-in-time snapshot, so re-running would write today's answer
        // under an old date. Skipping is the whole rule, not an optimisation.
        if (await _stats.ExistsForAsync(statDate, cancellationToken))
        {
            return false;
        }

        DateTimeOffset dayStart = new(statDate, TimeOnly.MinValue, TimeSpan.Zero);
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        IQueryable<Gym> livingGyms = _dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Status != GymStatus.Deleted);

        var counts = new GymCounts(
            Total: await livingGyms.CountAsync(cancellationToken),
            Active: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Active, cancellationToken),
            Suspended: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Suspended, cancellationToken),
            Provisioning: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Provisioning, cancellationToken),

            // The three event counts are the day's, not the running total — they are what a growth
            // or churn chart plots, and each is dated by a column rather than inferred.
            New: await livingGyms.CountAsync(
                gym => gym.CreatedAtUtc >= dayStart && gym.CreatedAtUtc < dayEnd, cancellationToken),
            Canceled: await _dbContext.Set<GymSubscription>().AsNoTracking().CountAsync(
                subscription => subscription.Status == SubscriptionStatus.Canceled
                    && subscription.CanceledAtUtc >= dayStart && subscription.CanceledAtUtc < dayEnd,
                cancellationToken),

            // Expiry has no timestamp of its own: the renewal sweep expires a subscription *at* its
            // period end, which is therefore the business date of the event.
            Expired: await _dbContext.Set<GymSubscription>().AsNoTracking().CountAsync(
                subscription => subscription.Status == SubscriptionStatus.Expired
                    && subscription.CurrentPeriodEndUtc >= dayStart && subscription.CurrentPeriodEndUtc < dayEnd,
                cancellationToken));

        var contributions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => SubscriptionStatusTransitions.Billing.Contains(subscription.Status))
            .Select(subscription => new { subscription.Status, subscription.BillingCycle, subscription.Price })
            .ToListAsync(cancellationToken);

        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            contributions.Select(row => new MrrContribution(row.Status, row.BillingCycle, row.Price)));

        _stats.Add(DailyPlatformStats.Record(statDate, counts, mrr, now));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
```

- [ ] **Step 2: Write the job**

Create `src/Platform/GymOS.Platform.Infrastructure/Jobs/PlatformStatsJob.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Infrastructure.Analytics;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// Records yesterday (M5). Nightly at 03:30 UTC, after <c>RetentionJob</c>'s 03:10.
///
/// <para>Yesterday rather than today because a day is only countable once it is over, and
/// <b>written once</b> because the counts are point-in-time: see <c>DailyPlatformStats</c>. A missed
/// night is a gap in the series and stays one — the alternative is a row invented from today's
/// state, which is worse than an honest hole in a chart.</para>
/// </summary>
internal sealed partial class PlatformStatsJob : IScheduledJob
{
    private readonly DailyStatsCompiler _compiler;
    private readonly IDatabaseClock _clock;
    private readonly ILogger<PlatformStatsJob> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform stats recorded for {StatDate}.")]
    private static partial void LogRecorded(ILogger logger, DateOnly statDate);

    [LoggerMessage(Level = LogLevel.Debug, Message =
        "Platform stats for {StatDate} already exist; a recorded day is never rewritten.")]
    private static partial void LogAlreadyRecorded(ILogger logger, DateOnly statDate);

    public PlatformStatsJob(DailyStatsCompiler compiler, IDatabaseClock clock, ILogger<PlatformStatsJob> logger)
    {
        _compiler = compiler;
        _clock = clock;
        _logger = logger;
    }

    public string Name => "platform-stats";

    public JobSchedule Schedule => JobSchedule.DailyAtUtc(new TimeOnly(3, 30));

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The database's clock, not this host's: the row is read as history forever, and a host with
        // a fast clock would otherwise file a day under the wrong date (the M3.4 reasoning).
        DateTimeOffset databaseNow = await _clock.UtcNowAsync(cancellationToken);
        DateOnly yesterday = DateOnly.FromDateTime(databaseNow.UtcDateTime).AddDays(-1);

        if (await _compiler.CompileAsync(yesterday, databaseNow, cancellationToken))
        {
            LogRecorded(_logger, yesterday);
        }
        else
        {
            LogAlreadyRecorded(_logger, yesterday);
        }
    }
}
```

- [ ] **Step 3: Register both**

In `PlatformAnalyticsServiceCollectionExtensions.AddPlatformAnalytics`, before the `return`:

```csharp
        services.TryAddScoped<DailyStatsCompiler>();
        services.AddScheduledJob<PlatformStatsJob>();
```

Add `using GymOS.BuildingBlocks.Infrastructure.Jobs;`, `using GymOS.Platform.Infrastructure.Analytics;` and `using GymOS.Platform.Infrastructure.Jobs;` to that file.

- [ ] **Step 4: Write the integration tests**

**These run over real PostgreSQL rather than against fakes**, which is a deliberate departure from the design's §10 wording ("the job's decision through fakes"). Every number this component produces *is* a SQL count; a fake repository would test the fake's arithmetic and prove nothing about the query. This is the M4 lesson applied in advance — the `PlanRepository` missing-`Include` defect was invisible to every test that did not use the real database.

Create `tests/GymOS.IntegrationTests/PlatformStatsJobTests.cs`. Mirror `RetentionJobTests.cs`'s fixture shape (read it first — it resolves the job from a scope with `Jobs:Enabled=false` so the host does not also run it). Tests:

```csharp
    [Fact]
    public async Task ItRecordsTheDayItIsGiven()
    {
        await SeedGymOnMonthlyPlanAsync("stats-gym", 49.99m, "EUR");

        Assert.True(await CompileAsync(Yesterday));

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(1, stats.TotalGyms);
        Assert.Equal(Money.Of(49.99m, Currency.Of("EUR")), Assert.Single(stats.Mrr).Amount);
    }

    [Fact]
    public async Task ASecondRunLeavesTheRowExactlyAsItWas()
    {
        // The design's central claim. Running again after the world has changed must not rewrite
        // history with today's answer.
        await SeedGymOnMonthlyPlanAsync("first-gym", 49.99m, "EUR");
        Assert.True(await CompileAsync(Yesterday));
        DailyPlatformStats before = await ReadAsync(Yesterday);

        await SeedGymOnMonthlyPlanAsync("second-gym", 100m, "EUR");
        Assert.False(await CompileAsync(Yesterday));

        DailyPlatformStats after = await ReadAsync(Yesterday);
        Assert.Equal(before.TotalGyms, after.TotalGyms);
        Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc);
        Assert.Equal(Money.Of(49.99m, Currency.Of("EUR")), Assert.Single(after.Mrr).Amount);
    }

    [Fact]
    public async Task AMissedDayStaysMissingRatherThanBeingBackfilled()
    {
        await CompileAsync(Yesterday);

        // Nothing invents the day before: a gap is discoverable, an invented row is not.
        await using var context = _host.CreateDbContext();
        Assert.False(await context.Set<DailyPlatformStats>()
            .AnyAsync(stats => stats.StatDate == Yesterday.AddDays(-1)));
    }

    [Fact]
    public async Task ATrialContributesNothingButStillCountsAsAGym()
    {
        await SeedGymOnTrialAsync("trial-gym", 49.99m, "EUR");

        await CompileAsync(Yesterday);

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(1, stats.TotalGyms);
        Assert.Empty(stats.Mrr);
    }

    [Fact]
    public async Task AScheduledCancellationStillCountsUntilItActuallyEnds()
    {
        // The gym has paid to the period end and the subscription is still Active, so the money is
        // still real. This case cannot be tested on the calculator — MrrContribution carries only
        // status, cycle and price, and a scheduled cancellation is invisible in all three — so it
        // belongs here, where the subscription is loaded.
        Guid gym = await SeedGymOnMonthlyPlanAsync("leaving-gym", 30m, "EUR");
        await ScheduleCancellationAsync(gym);

        await CompileAsync(Yesterday);

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(Money.Of(30m, Currency.Of("EUR")), Assert.Single(stats.Mrr).Amount);
    }

    [Fact]
    public async Task ADeletedGymIsCountedNowhere()
    {
        await SeedDeletedGymAsync("gone-gym");

        await CompileAsync(Yesterday);

        DailyPlatformStats stats = await ReadAsync(Yesterday);
        Assert.Equal(0, stats.TotalGyms);
    }
```

Write the `SeedGymOnMonthlyPlanAsync`, `SeedGymOnTrialAsync`, `SeedDeletedGymAsync`, `ScheduleCancellationAsync`, `CompileAsync` and `ReadAsync` helpers on top of **`PlatformApiHelpers`** (Task 0) — do not copy helper bodies from another test file; the shared surface exists precisely so this task does not. Its signatures are in Task 0's report file. Reads use `_host.CreateDbContext()`; `CompileAsync` resolves `DailyStatsCompiler` from a scope and calls it with a fixed `now`.

- [ ] **Step 5: Run them**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~PlatformStatsJobTests"
```

Expected: **PASS**, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Platform/GymOS.Platform.Infrastructure tests/GymOS.IntegrationTests/PlatformStatsJobTests.cs
git commit -m "M5 (3/6): the nightly job — yesterday, once, from the database's clock"
```

---

### Task 4: `analytics.read` and the dashboard endpoints

**Files:**
- Modify: `src/Platform/GymOS.Platform.Domain/Identity/PlatformPermissions.cs`
- Modify: `src/Platform/GymOS.Platform.Domain/Identity/SystemRoles.cs`
- Create: `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IDashboardQueries.cs`
- Create: `src/Platform/GymOS.Platform.Application/Analytics/DashboardQueriesHandlers.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/DashboardQueries.cs`
- Create: `src/Host/GymOS.WebApi/Endpoints/DashboardEndpoints.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs` (`app.MapDashboardEndpoints();`)
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformAnalyticsServiceCollectionExtensions.cs`
- Modify: `contracts/openapi-v1.json` (regenerated, not edited)
- Test: `tests/GymOS.IntegrationTests/DashboardApiTests.cs`

**Interfaces:**
- Consumes: `MonthlyRecurringRevenue`, `PlatformPermissions`, `IDispatcher`, `WriteResults`/`ToProblem`.
- Produces:
  - `PlatformPermissions.AnalyticsRead` = `"analytics.read"`
  - `record DashboardSummary(int TotalGyms, int ActiveGyms, int SuspendedGyms, int ProvisioningGyms, int LiveSubscriptions, IReadOnlyList<MoneyDto> Mrr)`
  - `record MoneyDto(decimal Amount, string Currency)`
  - `GetDashboardSummaryQuery`, `GetDashboardHealthQuery`, `GetRecentActivityQuery(int Limit)`

- [ ] **Step 1: Declare the permission**

In `PlatformPermissions.cs`, beside the feature block:

```csharp
    /// <summary>
    /// The dashboard's numbers and the analytics series (§3.2, §3.8).
    ///
    /// <para>Deliberately <b>not</b> granted to SupportAgent. MRR and churn are commercially
    /// sensitive, and the support question this platform exists to answer — "why can this gym not do
    /// X?" — needs the feature resolution, not the revenue. Same authority reasoning that keeps
    /// <c>features.manage</c> apart from <c>billing.manage</c>.</para>
    /// </summary>
    public const string AnalyticsRead = "analytics.read";
```

and in `All`:

```csharp
        new(AnalyticsRead, ModuleName, "View the dashboard summary and analytics series"),
```

In `SystemRoles.cs`, add `PlatformPermissions.AnalyticsRead` to **SuperAdmin, PlatformAdmin, BillingAdmin and ReadOnly**, and **not** SupportAgent. Add a comment at the SupportAgent block:

```csharp
                // Deliberately no analytics.read: a support agent explains a gym's features, and
                // does not need the platform's revenue to do it.
```

- [ ] **Step 2: Write the failing permission test**

Add to a new `tests/GymOS.IntegrationTests/DashboardApiTests.cs`. Mirror `FeatureCatalogueApiTests.cs`'s **fixture** (the `IAsyncLifetime` + isolated-database setup), and use **`PlatformApiHelpers`** (Task 0) for `SignedInAsync`, `CreateGymAsync`, `CreatePlanAsync` and `AssignAsync` — its signatures are in Task 0's report file. Do not copy helper bodies:

```csharp
    [Fact]
    public async Task SupportAgentIsRefusedTheDashboardButKeepsTheAuditFeed()
    {
        // SupportAgent already holds audit.read and not analytics.read, which makes it the exact
        // role that proves the boundary runs in both directions: refused the revenue, still able to
        // read the activity feed it needs to do its job.
        using HttpClient supportAgent = await SignedInAsync("SupportAgent", "support.dashboard@gymos.test");

        using HttpResponseMessage summary = await supportAgent.GetAsync(Summary);
        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);

        // /activity is the audit log in a trimmed shape, so audit.read governs it — analytics access
        // must not quietly become audit access, and audit access must not depend on analytics.
        using HttpResponseMessage activity = await supportAgent.GetAsync(Activity);
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
    }

    [Fact]
    public async Task TheSummaryIsLiveRatherThanYesterdaysRow()
    {
        JsonElement before = await SummaryAsync();
        int wasTotal = before.GetProperty("totalGyms").GetInt32();

        await CreateGymAsync("live-summary-gym");

        // No stats job has run. A dashboard that reported yesterday's count would be wrong, which is
        // why this endpoint does not read the read model.
        JsonElement after = await SummaryAsync();
        Assert.Equal(wasTotal + 1, after.GetProperty("totalGyms").GetInt32());
    }

    [Fact]
    public async Task TheSummarysMrrIsPerCurrencyAndExcludesTrials()
    {
        Guid plan = await CreatePlanAsync("DASH", 49.99m, "EUR");
        Guid gym = await CreateGymAsync("paying-gym");
        await AssignAsync(gym, plan);

        JsonElement mrr = (await SummaryAsync()).GetProperty("mrr");

        JsonElement eur = Assert.Single(mrr.EnumerateArray());
        Assert.Equal("EUR", eur.GetProperty("currency").GetString());
        Assert.Equal(49.99m, eur.GetProperty("amount").GetDecimal());
    }
```

- [ ] **Step 3: Run it and watch it fail**

```
dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~DashboardApiTests"
```

Expected: build error / 404 — the endpoints do not exist yet.

- [ ] **Step 4: Write the query contracts and handlers**

Create `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IDashboardQueries.cs`:

```csharp
namespace GymOS.Platform.Application.Analytics.Abstractions;

/// <summary>An amount with its currency, because a bare decimal has no meaning across a wire (R4).</summary>
public sealed record MoneyDto(decimal Amount, string Currency);

/// <summary>
/// What is true right now (§3.2). Read live from the operational tables: AD-8's read model exists so
/// dashboards never scan <em>tenant</em> tables, and these counts are O(gyms). A summary served from
/// yesterday's row would not be a trade-off, it would be wrong.
/// </summary>
public sealed record DashboardSummary(
    int TotalGyms,
    int ActiveGyms,
    int SuspendedGyms,
    int ProvisioningGyms,
    int LiveSubscriptions,
    IReadOnlyList<MoneyDto> Mrr);

public sealed record HealthCheckStatus(string Name, string Status);

public sealed record DashboardHealth(string Status, IReadOnlyList<HealthCheckStatus> Checks);

public sealed record ActivityEntry(
    DateTimeOffset OccurredAtUtc, string Action, string EntityType, string? UserEmail);

public interface IDashboardQueries
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEntry>> GetRecentActivityAsync(int limit, CancellationToken cancellationToken);
}
```

Create `src/Platform/GymOS.Platform.Application/Analytics/DashboardQueriesHandlers.cs` with `GetDashboardSummaryQuery` (`[RequiresPermission(PlatformPermissions.AnalyticsRead)]`) and `GetRecentActivityQuery(int Limit)` (`[RequiresPermission(PlatformPermissions.AuditRead)]`), each a one-line handler delegating to `IDashboardQueries` and returning `Result.Success(...)`. Copy the exact shape of `ListFeaturesQueryHandler` in `src/Platform/GymOS.Platform.Application/Features/FeatureCommands.cs` — same using block, same `ArgumentNullException.ThrowIfNull(query)`, same return.

Clamp the activity limit in the handler: `int limit = Math.Clamp(query.Limit, 1, 100);` with the comment `// An unbounded limit is a table scan a caller can request by accident.`

- [ ] **Step 5: Implement the live query**

Create `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/DashboardQueries.cs`. `GetSummaryAsync` counts non-deleted gyms by status, counts live subscriptions with `SubscriptionStatusTransitions.Live`, loads the billing subscriptions' `(Status, BillingCycle, Price)`, and calls **`MonthlyRecurringRevenue.Calculate`** — the same calculator the compiler uses, which is the point. Map each `Money` to `new MoneyDto(amount.Amount, amount.Currency.Code)`.

`GetRecentActivityAsync` projects the newest `limit` rows of `platform.audit_logs` into `ActivityEntry`, ordered by `created_at_utc` descending. Read `AuditLogQueries.cs` first and reuse its projection shape rather than inventing a second one.

Register both in `AddPlatformAnalytics`:

```csharp
        services.TryAddScoped<IDashboardQueries, DashboardQueries>();
```

- [ ] **Step 6: Write the endpoints**

Create `src/Host/GymOS.WebApi/Endpoints/DashboardEndpoints.cs` following `FeatureEndpoints.cs` exactly — `NewApiVersionSet("Dashboard")`, a `/api/v{version:apiVersion}/dashboard` group, `.WithTags("Dashboard")`, and three `MapGet`s:

- `/summary` → `RequireAuthorization(PlatformPermissions.AnalyticsRead)`, `.WithName("GetDashboardSummary")`, description explaining it is **live** and that `mrr` is per currency with no conversion.
- `/health` → `RequireAuthorization(PlatformPermissions.AnalyticsRead)`, `.WithName("GetDashboardHealth")`. Inject `HealthCheckService`, run it, and project `new DashboardHealth(report.Status.ToString(), report.Entries.Select(entry => new HealthCheckStatus(entry.Key, entry.Value.Status.ToString())).ToList())`. **Do not** include `entry.Value.Exception` or `Description` — the description on the endpoint should say so.
- `/activity` → `RequireAuthorization(PlatformPermissions.AuditRead)`, `.WithName("GetRecentActivity")`, `int limit = 20` from query.

Add `app.MapDashboardEndpoints();` in `Program.cs` after `app.MapFeatureEndpoints();`.

- [ ] **Step 7: Run the tests, then regenerate the contract**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~DashboardApiTests"
```

Expected: **PASS**, 3 tests. Then:

```
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~OpenApiContractTests"
git diff contracts/openapi-v1.json
```

**Read the diff semantically.** Expected: exactly three operations added (`GetDashboardSummary`, `GetDashboardHealth`, `GetRecentActivity`), nothing removed. If anything else changed, stop and find out why — the gate fails on any change by design.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "M5 (4/6): analytics.read, and a dashboard that reads live"
```

---

### Task 5: The analytics series

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Analytics/Rules/AnalyticsRangeRules.cs`
- Create: `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IAnalyticsQueries.cs`
- Create: `src/Platform/GymOS.Platform.Application/Analytics/AnalyticsQueryHandlers.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/AnalyticsQueries.cs`
- Create: `src/Host/GymOS.WebApi/Endpoints/AnalyticsEndpoints.cs`
- Modify: `Program.cs`, `PlatformAnalyticsServiceCollectionExtensions.cs`, `contracts/openapi-v1.json`
- Test: `tests/GymOS.Platform.Domain.UnitTests/AnalyticsRangeTests.cs`
- Test: `tests/GymOS.IntegrationTests/AnalyticsApiTests.cs`

**Interfaces:**
- Consumes: `DailyPlatformStats`, `MoneyDto`, `PlatformPermissions.AnalyticsRead`.
- Produces:
  - `AnalyticsRange.Validate(DateOnly from, DateOnly to)` → `Result` (error code `analytics.range_invalid`)
  - `record RevenuePoint(DateOnly Date, IReadOnlyList<MoneyDto> Mrr)`
  - `record GrowthPoint(DateOnly Date, int TotalGyms, int ActiveGyms, int SuspendedGyms, int ProvisioningGyms, int NewGyms)`
  - `record ChurnPoint(DateOnly Date, int Canceled, int Expired, int Total)`

- [ ] **Step 1: Write the failing range test**

Create `tests/GymOS.Platform.Domain.UnitTests/AnalyticsRangeTests.cs`:

```csharp
using GymOS.Platform.Domain.Analytics.Rules;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class AnalyticsRangeTests
{
    [Fact]
    public void AnOrderedRangeWithinTheCeilingIsAccepted()
    {
        Assert.True(AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1)).IsSuccess);
    }

    [Fact]
    public void ABackwardsRangeIsRefused()
    {
        Result result = AnalyticsRange.Validate(new DateOnly(2026, 3, 1), new DateOnly(2026, 1, 1));

        Assert.True(result.IsFailure);
        Assert.Equal("analytics.range_invalid", result.Error!.Code);
    }

    [Fact]
    public void ARangeLongerThanAYearIsRefused()
    {
        // A stated ceiling is a refusal the caller can act on; an unbounded range is a page that
        // gets slower every year until someone notices.
        Result result = AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 3));

        Assert.True(result.IsFailure);
        Assert.Equal("analytics.range_invalid", result.Error!.Code);
    }

    [Fact]
    public void ExactlyTheCeilingIsAllowed()
    {
        Assert.True(AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(365)).IsSuccess);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~AnalyticsRangeTests"
```

Expected: build error.

- [ ] **Step 3: Implement the rule**

Create `src/Platform/GymOS.Platform.Domain/Analytics/Rules/AnalyticsRangeRules.cs`:

```csharp
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Domain.Analytics.Rules;

/// <summary>
/// The bounds every analytics series accepts. Both ends are required and the span is capped, because
/// the table grows a row a day forever and a default range would eventually become a slow scan
/// nobody chose.
/// </summary>
public static class AnalyticsRange
{
    /// <summary>366 days — a full year including a leap day, inclusive of both ends.</summary>
    public const int MaximumDays = 366;

    public static Result Validate(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return Result.Failure(Error.Validation(
                "analytics.range_invalid", "`to` must not be earlier than `from`."));
        }

        return to.DayNumber - from.DayNumber + 1 > MaximumDays
            ? Result.Failure(Error.Validation(
                "analytics.range_invalid",
                $"An analytics range covers at most {MaximumDays} days. Ask for a shorter period."))
            : Result.Success();
    }
}
```

- [ ] **Step 4: Confirm it passes**

```
dotnet test tests/GymOS.Platform.Domain.UnitTests --filter "FullyQualifiedName~AnalyticsRangeTests"
```

Expected: **PASS**, 4 tests.

- [ ] **Step 5: Write the queries, handlers and endpoints**

Create `src/Platform/GymOS.Platform.Application/Analytics/Abstractions/IAnalyticsQueries.cs`:

```csharp
namespace GymOS.Platform.Application.Analytics.Abstractions;

public sealed record RevenuePoint(DateOnly Date, IReadOnlyList<MoneyDto> Mrr);

public sealed record GrowthPoint(
    DateOnly Date, int TotalGyms, int ActiveGyms, int SuspendedGyms, int ProvisioningGyms, int NewGyms);

/// <summary>
/// Churn with its two causes kept apart: <paramref name="Canceled"/> is a decision the customer
/// made, <paramref name="Expired"/> is a payment that failed. <paramref name="Total"/> is offered so
/// no caller has to add them, not so anyone has to choose between them.
/// </summary>
public sealed record ChurnPoint(DateOnly Date, int Canceled, int Expired, int Total);

/// <summary>Read access to the recorded series (§3.8). Only <c>daily_platform_stats</c> is touched.</summary>
public interface IAnalyticsQueries
{
    Task<IReadOnlyList<RevenuePoint>> GetRevenueAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<GrowthPoint>> GetGymsGrowthAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChurnPoint>> GetChurnAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
```

`AnalyticsQueries` reads `daily_platform_stats` (with `.Include(stats => stats.Mrr)` for revenue only), filters `StatDate >= from && StatDate <= to`, orders by `StatDate`, and projects. The churn projection, as the shape for all three:

```csharp
    public async Task<IReadOnlyList<ChurnPoint>> GetChurnAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _dbContext.Set<DailyPlatformStats>()
            .AsNoTracking()
            .Where(stats => stats.StatDate >= from && stats.StatDate <= to)
            .OrderBy(stats => stats.StatDate)
            .Select(stats => new ChurnPoint(
                stats.StatDate,
                stats.CanceledGyms,
                stats.ExpiredGyms,
                stats.CanceledGyms + stats.ExpiredGyms))
            .ToListAsync(cancellationToken);
```

**The rule that matters: return only the days that exist.** Do not generate a point per calendar day. Put this comment on each projection:

```csharp
        // Only the days recorded. A missing day returned as zeros draws the chart to the floor and
        // reads as "we lost every gym that night"; a consumer that wants a continuous axis can see
        // the gap and decide for itself what to do with it.
```

Each handler carries `[RequiresPermission(PlatformPermissions.AnalyticsRead)]` and calls `AnalyticsRange.Validate(query.From, query.To)` first, returning the failure unchanged.

`AnalyticsEndpoints.cs` follows `FeatureEndpoints.cs`: group `/api/v{version:apiVersion}/analytics`, `.WithTags("Analytics")`, three `MapGet`s (`/revenue`, `/gyms-growth`, `/churn`) each taking required `DateOnly from` and `DateOnly to` from the query string, `RequireAuthorization(PlatformPermissions.AnalyticsRead)`, `.ProducesProblem(StatusCodes.Status400BadRequest)`.

`/churn`'s description must state that cancellations and expiries are reported separately **and why**: a decision the customer made versus a payment that failed are different problems, and the total alone cannot tell you which you have.

Register `IAnalyticsQueries` in `AddPlatformAnalytics`; add `app.MapAnalyticsEndpoints();` to `Program.cs`.

- [ ] **Step 6: Write the integration tests**

Create `tests/GymOS.IntegrationTests/AnalyticsApiTests.cs`. Seed rows directly with `_host.CreateDbContext()` and `DailyPlatformStats.Record(...)` — these endpoints read the read model, so seeding it directly is the honest unit of test here.

```csharp
    [Fact]
    public async Task AGapInTheSeriesIsReturnedAsAGapAndNeverAsZeros()
    {
        await SeedAsync(new DateOnly(2026, 8, 1), total: 10);
        await SeedAsync(new DateOnly(2026, 8, 3), total: 12);   // the 2nd is missing on purpose

        JsonElement series = await GetAsync("/api/v1/analytics/gyms-growth?from=2026-08-01&to=2026-08-03");

        Assert.Equal(2, series.GetArrayLength());
        Assert.Equal("2026-08-01", series[0].GetProperty("date").GetString());
        Assert.Equal("2026-08-03", series[1].GetProperty("date").GetString());
    }

    [Fact]
    public async Task ChurnReportsItsTwoCausesSeparately()
    {
        await SeedChurnAsync(new DateOnly(2026, 8, 1), canceled: 2, expired: 3);

        JsonElement point = (await GetAsync("/api/v1/analytics/churn?from=2026-08-01&to=2026-08-01"))[0];

        Assert.Equal(2, point.GetProperty("canceled").GetInt32());
        Assert.Equal(3, point.GetProperty("expired").GetInt32());
        Assert.Equal(5, point.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RevenueCarriesEachCurrencySeparately()
    {
        await SeedRevenueAsync(new DateOnly(2026, 8, 1), [(120m, "EUR"), (80m, "GBP")]);

        JsonElement mrr = (await GetAsync("/api/v1/analytics/revenue?from=2026-08-01&to=2026-08-01"))[0]
            .GetProperty("mrr");

        Assert.Equal(2, mrr.GetArrayLength());
    }

    [Fact]
    public async Task AnOverlongRangeIsRefusedRatherThanServedSlowly()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/analytics/revenue?from=2025-01-01&to=2026-12-31", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("analytics.range_invalid", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task SupportAgentCannotReadTheSeries()
    {
        using HttpClient supportAgent = await SignedInAsync("SupportAgent", "support.analytics@gymos.test");

        using HttpResponseMessage response = await supportAgent.GetAsync(
            new Uri("/api/v1/analytics/churn?from=2026-08-01&to=2026-08-01", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 7: Run, regenerate the contract, commit**

```
dotnet build GymOS.Backend.slnx
dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~AnalyticsApiTests"
UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --no-build --filter "FullyQualifiedName~OpenApiContractTests"
git diff contracts/openapi-v1.json
```

Expected: 5 tests pass; the contract diff adds exactly three operations (`GetRevenue`, `GetGymsGrowth`, `GetChurn`) and removes none.

```bash
git add -A
git commit -m "M5 (5/6): the three series — gaps stay gaps, churn keeps its causes"
```

---

### Task 6: Documentation, full verification, live run

**Files:**
- Create: `docs/BusinessRules/Analytics.md`
- Modify: `docs/BusinessRules/README.md` (index row)
- Modify: `docs/STATUS.md` (M5 row, "M5 as built" section, resume pointer, test counts)

- [ ] **Step 1: Write `docs/BusinessRules/Analytics.md`**

Follow `docs/BusinessRules/Features.md`'s structure. Cover: the live-versus-recorded split and why; the write-once rule and why a gap is honest; MRR's exact definition (included/excluded statuses, normalisation, half-even rounding, per-currency); churn's two causes; the permission split including why SupportAgent is excluded; the endpoint table; the stable error codes (`analytics.range_invalid`); and a "Deferred, deliberately" table listing every column and endpoint §1 of the design defers, each with what it waits on.

- [ ] **Step 2: Full verification**

```
dotnet build GymOS.Backend.slnx
```
Expected: **0 Warning(s), 0 Error(s)**.

Then every project, checking each output for `Aborted`:

```
dotnet test tests/GymOS.SharedKernel.UnitTests
dotnet test tests/GymOS.BuildingBlocks.UnitTests
dotnet test tests/GymOS.ArchitectureTests
dotnet test tests/GymOS.Platform.Domain.UnitTests
dotnet test tests/GymOS.Platform.Application.UnitTests
dotnet test tests/GymOS.Platform.Infrastructure.UnitTests
dotnet test tests/GymOS.IntegrationTests
```

Expected: no `Aborted` anywhere, every project's total matching its own count, and a grand total of **1267 + the tests this plan added** (10 + 4 + 2 + 6 + 3 + 4 + 5 = 34) = **1301**. If the total is lower, a run was truncated — re-run that project rather than reporting the number.

- [ ] **Step 3: Live run**

```
docker compose up -d --build
```

(The `--build` matters: compose keeps the old image, and a stale container answers 404 for new routes.) Then, signed in as `admin@gymos.local` / `gymos-dev-admin-password`:

1. Create two gyms, a plan priced in EUR, and assign both — `GET /api/v1/dashboard/summary` shows the gyms and one EUR MRR figure.
2. Put a third gym on a **trial** — `totalGyms` rises, `mrr` does not.
3. Run the compiler for yesterday by hand (`docker compose exec` is not needed; call the job's schedule by restarting with `Jobs__Enabled=true` and waiting, or invoke `DailyStatsCompiler` from an integration-test-style scope). Then `GET /api/v1/analytics/revenue?from=&to=` returns the recorded day.
4. Assign a fourth gym and read both endpoints again: **the summary moves, the series does not**. That is the live-versus-recorded split demonstrated rather than asserted, and it is the single most important thing this run proves.
5. `GET /api/v1/analytics/gyms-growth` over a range with a missing day returns fewer points than days.

Record the actual output in the STATUS "M5 as built" section — not a claim that it worked.

- [ ] **Step 4: Update STATUS.md**

Flip the M5 row to complete with the measured test count; add an "M5 as built" section covering the six tasks, the decisions taken, and the traps found; update the per-project counts block and the `dotnet test` expectation in "Fresh-machine setup"; rewrite "Next step (resume here)" to point at M5's review and then M6.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "M5 (6/6): business rules, full verification, and the live run"
```

- [ ] **Step 6: Stop for review**

Do not start M6. Report: what was built, the verified test count, what the live run showed, and the two deviations the design recorded (per-currency MRR, split churn) so the reviewer can accept or reject them deliberately.
