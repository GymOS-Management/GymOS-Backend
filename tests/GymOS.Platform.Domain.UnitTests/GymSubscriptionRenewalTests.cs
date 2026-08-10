using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// Period arithmetic under renewal: the anchor day survives month-end clamping (the Jan-31 gym is
/// a month-end gym forever, not a Feb-28 one), and a long trial's period catch-up walks the chain
/// without billing anything itself.
/// </summary>
public sealed class GymSubscriptionRenewalTests
{
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static GymSubscription MonthlyAssignedAt(DateTimeOffset now, int trialDays = 0)
    {
        SubscriptionPlan plan = SubscriptionPlan.Create("PRO", "PRO", "", trialDays, PlanLimits.Unlimited);
        plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));
        return GymSubscription.Assign(
            GymId, plan, SubscriptionTerms.From(plan.Prices.Single()), now);
    }

    [Fact]
    public void AMonthEndSignupKeepsItsMonthEndAnchorThroughClampedMonths()
    {
        // Jan 31 → Feb 28 is forced (February has no 31st) — but March has one, and a chain that
        // merely added months to the clamped date would bill this gym on the 28th forever.
        var subscription = MonthlyAssignedAt(new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);

        subscription.Renew();
        Assert.Equal(new DateTimeOffset(2026, 3, 31, 10, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);

        subscription.Renew();
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);

        subscription.Renew();
        Assert.Equal(new DateTimeOffset(2026, 5, 31, 10, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);

        // Periods stay gapless: each start is exactly the previous end.
        Assert.Equal(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodStartUtc);
    }

    [Fact]
    public void AMidMonthSignupRenewsOnItsDayEveryMonth()
    {
        var subscription = MonthlyAssignedAt(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));

        subscription.Renew();
        subscription.Renew();

        Assert.Equal(new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);
    }

    [Fact]
    public void AChangedBillingCycleReAnchorsAtTheBoundaryItTakesEffect()
    {
        var subscription = MonthlyAssignedAt(new DateTimeOffset(2026, 1, 31, 10, 0, 0, TimeSpan.Zero));
        DateTimeOffset boundary = subscription.CurrentPeriodEndUtc; // Feb 28

        SubscriptionPlan yearly = SubscriptionPlan.Create("YEARLY", "YEARLY", "", 0, PlanLimits.Unlimited);
        yearly.SetPrice(BillingCycle.Yearly, Money.Of(480m, Currency.Euro));
        subscription.ChangePlan(yearly, SubscriptionTerms.From(yearly.Prices.Single()));

        // The new cycle counts from the boundary where it took effect: anchored to Feb 28 now,
        // not to the old Jan 31 day.
        Assert.Equal(boundary, subscription.PeriodAnchorUtc);

        subscription.Renew();
        Assert.Equal(boundary, subscription.CurrentPeriodStartUtc);
        Assert.Equal(boundary.AddMonths(12), subscription.CurrentPeriodEndUtc);
    }

    [Fact]
    public void CatchUpAdvancesThroughEveryLapsedPeriodAndThenStops()
    {
        // A 100-day trial on a monthly plan lapses three period boundaries before conversion.
        var subscription = MonthlyAssignedAt(new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero), trialDays: 100);
        DateTimeOffset conversionDay = new(2026, 4, 25, 9, 0, 0, TimeSpan.Zero);

        subscription.CatchUpPeriods(conversionDay);

        Assert.Equal(new DateTimeOffset(2026, 4, 15, 9, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodStartUtc);
        Assert.Equal(new DateTimeOffset(2026, 5, 15, 9, 0, 0, TimeSpan.Zero), subscription.CurrentPeriodEndUtc);
        Assert.False(subscription.PeriodHasEnded(conversionDay));

        // Already current: nothing to do.
        subscription.DequeueDomainEvents();
        subscription.CatchUpPeriods(conversionDay);
        Assert.Empty(subscription.DomainEvents);
    }
}
