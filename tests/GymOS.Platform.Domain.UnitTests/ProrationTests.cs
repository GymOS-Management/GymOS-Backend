using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// The proration split, tested as properties rather than as arithmetic: what matters is that nothing
/// is created or lost between the credit and the charge, and that the sign of the net is stable —
/// because the sign is what decides whether a plan change is billed now or scheduled for the end of
/// the period.
/// </summary>
public sealed class ProrationTests
{
    private static readonly DateTimeOffset PeriodStart = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PeriodEnd = new(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly Currency Eur = Currency.Euro;

    private static ProrationResult Compute(decimal oldPrice, decimal newPrice, int daysIn) =>
        Proration.Compute(
            Money.Of(oldPrice, Eur),
            Money.Of(newPrice, Eur),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(daysIn))
        ?? throw new InvalidOperationException("Expected a proration for a period with days left.");

    // ── The identity the invoice depends on ──────────────────────────────────────

    [Theory]
    [InlineData(10, 20, 10)]
    [InlineData(20, 10, 10)]
    [InlineData(49.99, 99.99, 7)]
    [InlineData(99.99, 49.99, 23)]
    [InlineData(10, 10, 15)]
    public void TheNetIsExactlyTheChargeMinusTheCredit(decimal oldPrice, decimal newPrice, int daysIn)
    {
        ProrationResult result = Compute(oldPrice, newPrice, daysIn);

        // The invoice puts the credit and the charge on two lines and totals them. If the net were
        // computed any other way, the total on the document and the figure the decision was taken on
        // could differ by a cent.
        Assert.Equal(
            result.NewPlanRemainderCharge.Amount - result.UnusedOldPlanCredit.Amount,
            result.Net.Amount);
    }

    [Theory]
    [InlineData(10, 20, 10)]
    [InlineData(49.99, 99.99, 7)]
    public void NeitherSplitCreatesNorLosesACent(decimal oldPrice, decimal newPrice, int daysIn)
    {
        ProrationResult result = Compute(oldPrice, newPrice, daysIn);
        int[] ratios = [result.DaysUsed, result.DaysRemaining];

        Assert.Equal(
            Money.Of(oldPrice, Eur),
            Money.Of(oldPrice, Eur).Allocate(ratios).Aggregate((left, right) => left.Add(right)));
        Assert.Equal(
            Money.Of(newPrice, Eur),
            Money.Of(newPrice, Eur).Allocate(ratios).Aggregate((left, right) => left.Add(right)));
    }

    // ── The sign, which is the whole policy ──────────────────────────────────────

    [Fact]
    public void AnUpgradeNetsPositive()
    {
        ProrationResult result = Compute(oldPrice: 10m, newPrice: 20m, daysIn: 10);

        Assert.True(result.Net.IsPositive);
        Assert.Equal(Money.Of(6.67m, Eur), result.UnusedOldPlanCredit);
        Assert.Equal(Money.Of(13.33m, Eur), result.NewPlanRemainderCharge);
        Assert.Equal(Money.Of(6.66m, Eur), result.Net);
    }

    [Fact]
    public void ADowngradeNetsNegative()
    {
        ProrationResult result = Compute(oldPrice: 20m, newPrice: 10m, daysIn: 10);

        // Which is what routes it to "scheduled at period end" instead of "billed now" — the platform
        // issues no negative invoices and holds no credit balances.
        Assert.True(result.Net.IsNegative);
        Assert.Equal(Money.Of(-6.66m, Eur), result.Net);
    }

    [Fact]
    public void AChangeAtTheSamePriceNetsZero()
    {
        ProrationResult result = Compute(oldPrice: 49.99m, newPrice: 49.99m, daysIn: 12);

        Assert.True(result.Net.IsZero);
        Assert.Equal(result.UnusedOldPlanCredit, result.NewPlanRemainderCharge);
    }

    // ── Day boundaries ───────────────────────────────────────────────────────────

    [Fact]
    public void OnTheFirstDayOfThePeriodTheWholePriceIsCreditedAndCharged()
    {
        ProrationResult result = Compute(oldPrice: 10m, newPrice: 20m, daysIn: 0);

        Assert.Equal(0, result.DaysUsed);
        Assert.Equal(30, result.DaysRemaining);
        Assert.Equal(Money.Of(10m, Eur), result.UnusedOldPlanCredit);
        Assert.Equal(Money.Of(20m, Eur), result.NewPlanRemainderCharge);
        Assert.Equal(Money.Of(10m, Eur), result.Net);
    }

    [Fact]
    public void APartDayCountsAsNotYetUsed()
    {
        // Days used floors: the gym is not charged for a day it is halfway through, and the same count
        // drives both the credit and the charge, so the choice cannot introduce a sign error.
        ProrationResult result = Proration.Compute(
            Money.Of(10m, Eur),
            Money.Of(20m, Eur),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(10).AddHours(12))!;

        Assert.Equal(10, result.DaysUsed);
        Assert.Equal(20, result.DaysRemaining);
    }

    [Fact]
    public void OnTheLastDayThereIsNothingToProrate() =>
        Assert.Null(Proration.Compute(
            Money.Of(10m, Eur), Money.Of(20m, Eur), PeriodStart, PeriodEnd, PeriodEnd));

    [Fact]
    public void AfterThePeriodEndsThereIsNothingToProrate() =>
        Assert.Null(Proration.Compute(
            Money.Of(10m, Eur), Money.Of(20m, Eur), PeriodStart, PeriodEnd, PeriodEnd.AddDays(3)));

    [Fact]
    public void AZeroLengthPeriodHasNothingToProrate() =>
        Assert.Null(Proration.Compute(
            Money.Of(10m, Eur), Money.Of(20m, Eur), PeriodStart, PeriodStart, PeriodStart));

    [Fact]
    public void BeforeThePeriodStartsNothingHasBeenUsed()
    {
        // A clock skew or a backdated request must not produce a negative day count, which would make
        // Allocate throw and turn a plan change into a 500.
        ProrationResult result = Proration.Compute(
            Money.Of(10m, Eur),
            Money.Of(20m, Eur),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(-2))!;

        Assert.Equal(0, result.DaysUsed);
        Assert.Equal(30, result.DaysRemaining);
    }

    // ── Currencies ───────────────────────────────────────────────────────────────

    [Fact]
    public void PricesInTwoCurrenciesAreRefused() =>
        Assert.Throws<DomainException>(() => Proration.Compute(
            Money.Of(10m, Eur),
            Money.Of(20m, Currency.Of("USD")),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(10)));

    [Fact]
    public void ACurrencyWithNoMinorUnitSplitsIntoWholeUnits()
    {
        Currency jpy = Currency.Of("JPY");

        ProrationResult result = Proration.Compute(
            Money.Of(3000m, jpy),
            Money.Of(6000m, jpy),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(10))!;

        Assert.Equal(Money.Of(2000m, jpy), result.UnusedOldPlanCredit);
        Assert.Equal(Money.Of(4000m, jpy), result.NewPlanRemainderCharge);
        Assert.Equal(Money.Of(2000m, jpy), result.Net);
        Assert.Equal(0m, result.Net.Amount % 1m);
    }

    [Fact]
    public void ACurrencyWithThreeMinorUnitsSplitsIntoThousandths()
    {
        Currency kwd = Currency.Of("KWD");

        ProrationResult result = Proration.Compute(
            Money.Of(10m, kwd),
            Money.Of(20m, kwd),
            PeriodStart,
            PeriodEnd,
            PeriodStart.AddDays(7))!;

        // 23/30 of KWD 10 is 7.666… — a currency rounded to cents would report 7.67 and lose a mil
        // against the charge computed the same way.
        Assert.Equal(Money.Of(7.667m, kwd), result.UnusedOldPlanCredit);
        Assert.Equal(
            result.NewPlanRemainderCharge.Amount - result.UnusedOldPlanCredit.Amount,
            result.Net.Amount);
    }

    [Fact]
    public void TheDayCountsAreTheOnesTheInvoiceDescribes()
    {
        ProrationResult result = Compute(oldPrice: 10m, newPrice: 20m, daysIn: 7);

        // The credit line says "N of M days"; if these did not add up to the period the gym would read
        // a sentence that does not describe its own invoice.
        Assert.Equal(7, result.DaysUsed);
        Assert.Equal(23, result.DaysRemaining);
        Assert.Equal(30, result.DaysUsed + result.DaysRemaining);
    }
}
