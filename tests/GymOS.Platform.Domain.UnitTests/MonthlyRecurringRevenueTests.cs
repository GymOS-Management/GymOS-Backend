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
