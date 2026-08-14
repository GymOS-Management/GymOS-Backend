using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
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
    public void AnOutOfRangeBillingCycleIsADomainExceptionNotADivideByZero()
    {
        // (BillingCycle)0 is not a value any code path produces — it can only arrive as corrupt
        // stored data. Casting straight to an int divisor would throw DivideByZeroException for 0
        // and silently divide by the wrong number for anything else undefined; both are invariant
        // breaches, so this must surface as a named DomainException instead.
        var corrupt = new MrrContribution(SubscriptionStatus.Active, (BillingCycle)0, Money.Of(10m, Eur));

        DomainException exception = Assert.Throws<DomainException>(
            () => MonthlyRecurringRevenue.Calculate([corrupt]));

        Assert.Contains("0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBillingSetIsActiveAndPastDueByDesign()
    {
        // Billing is not derived from Live; these are independent sets answering different questions.
        // Live: "does the one-per-gym rule apply". Billing: "does this contribute revenue".
        Assert.Equal(
            [SubscriptionStatus.Active, SubscriptionStatus.PastDue],
            SubscriptionStatusTransitions.Billing);

        // But Billing must be a subset of Live—a non-live subscription cannot contribute revenue.
        // This invariant can fail and should fail if Billing is ever extended.
        Assert.True(SubscriptionStatusTransitions.Billing.All(status => SubscriptionStatusTransitions.IsLive(status)));

        // Trialing is live but not billing, confirming the sets are independent.
        Assert.True(SubscriptionStatusTransitions.IsLive(SubscriptionStatus.Trialing));
        Assert.False(SubscriptionStatusTransitions.IsBilling(SubscriptionStatus.Trialing));
    }


    [Fact]
    public void AnAnnualPriceNormalisesByDivision_NotByAReciprocal()
    {
        // The bug this test exists for: PerMonth used `Multiply(1m / months, HalfEven)`. `1m / 12`
        // is not representable, so 599.94 * (1m/12) = 49.994999... and half-even rounded it DOWN to
        // 49.99, where 599.94 / 12 is exactly 49.995 and must round to 50.00. An exhaustive scan of
        // two-decimal annual prices found 1 in 24 affected — and every one of them low, never high,
        // so the platform under-reported its own revenue in one direction only.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly,
                Money.Of(599.94m, Currency.Euro))]);

        Assert.Equal(50.00m, Assert.Single(mrr).Amount);
    }

    [Theory]
    [InlineData(599.94, 50.00)]
    [InlineData(12.18, 1.02)]
    [InlineData(299.94, 25.00)]
    [InlineData(1199.94, 100.00)]
    [InlineData(1200.18, 100.02)]
    [InlineData(1212.18, 101.02)]
    public void EveryKnownReciprocalCasualtyNormalisesCorrectly(decimal annual, decimal expected)
    {
        // Each of these came out a cent low under the reciprocal. They are pinned as a set because
        // the failure was systematic, not a single unlucky price.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly,
                Money.Of(annual, Currency.Euro))]);

        Assert.Equal(expected, Assert.Single(mrr).Amount);
    }

    [Fact]
    public void AZeroDecimalCurrencyOnAMidpointRoundsToTheEvenWholeUnit()
    {
        // The existing zero-decimal test uses JPY 12,000 — an exact multiple of twelve, which cannot
        // exhibit the reciprocal bug at all. 59,994 / 12 is exactly 4,999.5, so it can: the
        // reciprocal form gave 4,999 and half-even on the true quotient gives the even 5,000.
        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            [new MrrContribution(SubscriptionStatus.Active, BillingCycle.Yearly,
                Money.Of(59_994m, Currency.Of("JPY")))]);

        Assert.Equal(5_000m, Assert.Single(mrr).Amount);
    }

}
