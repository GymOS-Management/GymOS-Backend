using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding.DemoData;

/// <summary>One plan in the demo catalogue. Prices are in the portfolio's single currency.</summary>
internal sealed record DemoPlan(
    string Code,
    string Name,
    string Description,
    int TrialDays,
    PlanLimits Limits,
    int SortOrder,
    IReadOnlyList<DemoPrice> Prices,
    IReadOnlyList<string> EnabledFeatureKeys);

/// <summary>A listed price; <see cref="Money"/>, never a naked decimal (AD-42).</summary>
internal sealed record DemoPrice(BillingCycle Cycle, Money Price);

internal sealed record DemoOverride(string FeatureKey, bool IsEnabled, string Reason);

/// <summary>
/// A gym's subscription as a timeline of instants. Every state the seeder produces, and every state
/// <see cref="DemoTimeline"/> reports for a past day, is read off these with <see cref="StatusAt"/>,
/// so the rows written to <c>gym_subscriptions</c> and the history written to
/// <c>daily_platform_stats</c> cannot disagree about when anything happened.
/// </summary>
internal sealed record DemoSubscription(
    string PlanCode,
    BillingCycle Cycle,
    DateTimeOffset AssignedAt,
    DateTimeOffset? ConvertedAt,
    DateTimeOffset? PastDueAt,
    DateTimeOffset? CanceledAt,
    string? CancellationReason,
    DateTimeOffset? ExpiredAt,
    bool VoidsLastInvoice)
{
    /// <summary>The status at <paramref name="at"/>, or <see langword="null"/> before assignment.</summary>
    public SubscriptionStatus? StatusAt(DateTimeOffset at)
    {
        if (AssignedAt > at)
        {
            return null;
        }

        if (CanceledAt is { } canceledAt && canceledAt <= at)
        {
            return SubscriptionStatus.Canceled;
        }

        if (ExpiredAt is { } expiredAt && expiredAt <= at)
        {
            return SubscriptionStatus.Expired;
        }

        if (PastDueAt is { } pastDueAt && pastDueAt <= at)
        {
            return SubscriptionStatus.PastDue;
        }

        // A plan without a trial converts at assignment (ConvertedAt == AssignedAt); a trial still
        // running has no conversion instant at all.
        return ConvertedAt is null || ConvertedAt > at
            ? SubscriptionStatus.Trialing
            : SubscriptionStatus.Active;
    }
}

/// <summary>A gym as a timeline of instants; see <see cref="DemoSubscription"/> for why.</summary>
internal sealed record DemoGym(
    string Slug,
    string Name,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    DateTimeOffset ProvisionedAt,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? SuspendedAt,
    string? SuspensionReason,
    DateTimeOffset? DeletedAt,
    DemoSubscription? Subscription,
    IReadOnlyList<DemoOverride> Overrides)
{
    /// <summary>
    /// The gym's status at <paramref name="at"/> for population counts, or <see langword="null"/>
    /// when it is not a tenant then — not yet provisioned, or already deleted. Mirrors
    /// <c>DailyStatsCompiler</c>, which counts the living population and excludes deleted gyms.
    /// </summary>
    public GymStatus? PopulationStatusAt(DateTimeOffset at)
    {
        if (ProvisionedAt > at)
        {
            return null;
        }

        if (DeletedAt is { } deletedAt && deletedAt <= at)
        {
            return null;
        }

        if (SuspendedAt is { } suspendedAt && suspendedAt <= at)
        {
            return GymStatus.Suspended;
        }

        if (ActivatedAt is { } activatedAt && activatedAt <= at)
        {
            return GymStatus.Active;
        }

        return GymStatus.Provisioning;
    }

    /// <summary>The instant of the last thing that happened to the gym row itself.</summary>
    public DateTimeOffset LastChangedAt =>
        new[] { ProvisionedAt, ActivatedAt ?? ProvisionedAt, SuspendedAt ?? ProvisionedAt, DeletedAt ?? ProvisionedAt }.Max();
}

/// <summary>
/// The demo portfolio (design: <c>docs/superpowers/specs/2026-09-03-demo-data-seeder-design.md</c> §5),
/// built relative to one instant so it is always recent: the newest gym was provisioned two days
/// ago, the trial ends next week, the overdue invoice is overdue now.
/// </summary>
internal sealed class DemoPortfolio
{
    public const string SlugPrefix = "demo-";

    public static readonly Currency Currency = Currency.Euro;

    /// <summary>When the plan catalogue is dated: before the first gym, so the history reads sensibly.</summary>
    public DateTimeOffset CatalogueCreatedAt { get; }

    public IReadOnlyList<DemoPlan> Plans { get; }

    public IReadOnlyList<DemoGym> Gyms { get; }

    private DemoPortfolio(DateTimeOffset catalogueCreatedAt, IReadOnlyList<DemoPlan> plans, IReadOnlyList<DemoGym> gyms)
    {
        CatalogueCreatedAt = catalogueCreatedAt;
        Plans = plans;
        Gyms = gyms;
    }

    public Money PriceOf(string planCode, BillingCycle cycle)
    {
        DemoPlan plan = Plans.Single(candidate => string.Equals(candidate.Code, planCode, StringComparison.Ordinal));
        return plan.Prices.Single(candidate => candidate.Cycle == cycle).Price;
    }

    public static DemoPortfolio Build(DateTimeOffset now)
    {
        // 09:00 UTC on the day, so no instant straddles a day boundary in the stats series and every
        // "days ago" is inside the calendar day it names, whatever time of day the seeder runs.
        DateTimeOffset At(int daysAgo) =>
            new(now.UtcDateTime.Date.AddDays(-daysAgo).AddHours(9), TimeSpan.Zero);

        static DemoSubscription Sub(
            string plan,
            BillingCycle cycle,
            DateTimeOffset assignedAt,
            DateTimeOffset? convertedAt,
            DateTimeOffset? pastDueAt = null,
            DateTimeOffset? canceledAt = null,
            string? reason = null,
            DateTimeOffset? expiredAt = null,
            bool voidsLastInvoice = false) =>
            new(plan, cycle, assignedAt, convertedAt, pastDueAt, canceledAt, reason, expiredAt, voidsLastInvoice);

        static DemoGym Gym(
            string slug,
            string name,
            string owner,
            string email,
            string phone,
            DateTimeOffset provisionedAt,
            DateTimeOffset? activatedAt,
            DemoSubscription? subscription,
            DateTimeOffset? suspendedAt = null,
            string? suspensionReason = null,
            DateTimeOffset? deletedAt = null,
            params DemoOverride[] overrides) =>
            new(slug, name, owner, email, phone, provisionedAt, activatedAt, suspendedAt, suspensionReason,
                deletedAt, subscription, overrides);

        const string Starter = "STARTER";
        const string Growth = "GROWTH";
        const string Enterprise = "ENTERPRISE";
        const int Trial = 14;

        static DemoPrice Eur(BillingCycle cycle, Money price) => new(cycle, price);
        static Money Euros(int amount) => Money.Of(amount, Currency);

        var plans = new List<DemoPlan>
        {
            new(Starter, "Starter", "For a single-site gym getting its front desk off paper.", Trial,
                new PlanLimits(MaxMembers: 150, MaxTrainers: 3, MaxStaff: 5, StorageLimitMb: 5 * 1024), 1,
                [Eur(BillingCycle.Monthly, Euros(49)), Eur(BillingCycle.Yearly, Euros(490))],
                [PlatformFeatures.Reports]),
            new(Growth, "Growth", "Sales, retention and reporting tools for a gym that is growing.", Trial,
                new PlanLimits(MaxMembers: 600, MaxTrainers: 10, MaxStaff: 15, StorageLimitMb: 25 * 1024), 2,
                [Eur(BillingCycle.Monthly, Euros(99)), Eur(BillingCycle.Quarterly, Euros(279)), Eur(BillingCycle.Yearly, Euros(990))],
                [PlatformFeatures.Pos, PlatformFeatures.Crm, PlatformFeatures.Marketing, PlatformFeatures.Analytics, PlatformFeatures.Reports]),
            new(Enterprise, "Enterprise", "Every module, no limits, for multi-site operators.", 0,
                PlanLimits.Unlimited, 3,
                [Eur(BillingCycle.Monthly, Euros(249)), Eur(BillingCycle.Yearly, Euros(2490))],
                [
                    PlatformFeatures.Pos, PlatformFeatures.Inventory, PlatformFeatures.Crm, PlatformFeatures.Nutrition,
                    PlatformFeatures.WorkoutBuilder, PlatformFeatures.MemberApp, PlatformFeatures.Gateway,
                    PlatformFeatures.AccessControl, PlatformFeatures.Marketing, PlatformFeatures.Analytics,
                    PlatformFeatures.Reports,
                ]),
        };

        var gyms = new List<DemoGym>
        {
            Gym($"{SlugPrefix}limassol-iron-works", "Iron Works Limassol", "Andreas Georgiou",
                "andreas@ironworks-limassol.example.com", "+357 25 100201", At(170), At(169),
                Sub(Growth, BillingCycle.Monthly, At(169), At(155))),

            Gym($"{SlugPrefix}nicosia-apex", "Apex Fitness Nicosia", "Maria Constantinou",
                "maria@apexfitness.example.com", "+357 22 100202", At(160), At(159),
                Sub(Enterprise, BillingCycle.Yearly, At(159), At(159))),

            Gym($"{SlugPrefix}larnaca-seaside", "Seaside Strength Larnaca", "Christos Ioannou",
                "christos@seasidestrength.example.com", "+357 24 100203", At(128), At(127),
                Sub(Starter, BillingCycle.Monthly, At(127), At(113))),

            Gym($"{SlugPrefix}paphos-olympus", "Olympus Gym Paphos", "Eleni Papadopoulou",
                "eleni@olympusgym.example.com", "+357 26 100204", At(140), At(139),
                Sub(Growth, BillingCycle.Quarterly, At(139), At(125), pastDueAt: At(20))),

            Gym($"{SlugPrefix}limassol-pulse", "Pulse Studio Limassol", "Nikos Charalambous",
                "nikos@pulsestudio.example.com", "+357 25 100205", At(120), At(119),
                Sub(Starter, BillingCycle.Monthly, At(119), At(105)),
                overrides: new DemoOverride(PlatformFeatures.AiAssistant, IsEnabled: true,
                    "Q3 pilot of the AI assistant, agreed with the owner; review at renewal.")),

            Gym($"{SlugPrefix}nicosia-forge", "The Forge Nicosia", "Sofia Kyriakou",
                "sofia@theforge.example.com", "+357 22 100206", At(100), At(99),
                Sub(Growth, BillingCycle.Monthly, At(99), At(85),
                    canceledAt: At(20), reason: "Closed the second location; consolidating on one site.")),

            Gym($"{SlugPrefix}ayia-napa-coastline", "Coastline Fitness Ayia Napa", "Petros Michael",
                "petros@coastlinefitness.example.com", "+357 23 100207", At(90), At(89),
                Sub(Starter, BillingCycle.Monthly, At(89), At(75), pastDueAt: At(40), expiredAt: At(10)),
                suspendedAt: At(10), suspensionReason: "Subscription expired after 30 days past due."),

            Gym($"{SlugPrefix}limassol-titan", "Titan Performance Limassol", "Georgia Demetriou",
                "georgia@titanperformance.example.com", "+357 25 100208", At(75), At(74),
                Sub(Enterprise, BillingCycle.Monthly, At(74), At(74)),
                overrides: new DemoOverride(PlatformFeatures.Pos, IsEnabled: false,
                    "Runs its own point of sale; GymOS handles memberships and billing only.")),

            Gym($"{SlugPrefix}larnaca-flow", "Flow Yoga & Pilates Larnaca", "Yiannis Antoniou",
                "yiannis@flowyoga.example.com", "+357 24 100209", At(60), At(59),
                Sub(Starter, BillingCycle.Yearly, At(59), At(45))),

            Gym($"{SlugPrefix}nicosia-crossbox", "CrossBox Strovolos", "Katerina Savva",
                "katerina@crossbox.example.com", "+357 22 100210", At(30), At(29),
                Sub(Growth, BillingCycle.Monthly, At(29), At(15))),

            Gym($"{SlugPrefix}paphos-harbour", "Harbour Health Club Paphos", "Michalis Pavlou",
                "michalis@harbourhealth.example.com", "+357 26 100211", At(6), At(5),
                Sub(Growth, BillingCycle.Monthly, At(5), convertedAt: null)),

            Gym($"{SlugPrefix}paralimni-eastside", "Eastside Gym Paralimni", "Anna Loizou",
                "anna@eastsidegym.example.com", "+357 23 100212", At(2), activatedAt: null, subscription: null),

            Gym($"{SlugPrefix}nicosia-old-town", "Old Town Boxing Nicosia", "Costas Stylianou",
                "costas@oldtownboxing.example.com", "+357 22 100213", At(130), At(129),
                Sub(Starter, BillingCycle.Monthly, At(129), At(115),
                    canceledAt: At(45), reason: "Owner retired; the gym has closed.", voidsLastInvoice: true),
                deletedAt: At(45)),
        };

        return new DemoPortfolio(At(185), plans, gyms);
    }
}
