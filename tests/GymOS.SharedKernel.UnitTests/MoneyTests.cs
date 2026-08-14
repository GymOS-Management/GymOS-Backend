using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

public sealed class MoneyTests
{
    private static readonly Currency Eur = Currency.Euro;
    private static readonly Currency Usd = Currency.Of("USD");
    private static readonly Currency Jpy = Currency.Of("JPY");
    private static readonly Currency Kwd = Currency.Of("KWD");

    private static Money Euros(decimal amount) => Money.Of(amount, Eur);

    // ── Construction and rounding ────────────────────────────────────────────────

    [Fact]
    public void ConstructionRoundsToTheCurrencysMinorUnits()
    {
        // An amount that cannot be settled is not a price: €10.005 is not payable, and carrying it
        // forward means the figure shown and the figure charged can differ by a cent.
        Assert.Equal(10.00m, Euros(10.004m).Amount);
        Assert.Equal(1000m, Money.Of(1000.4m, Jpy).Amount);
        Assert.Equal(1.235m, Money.Of(1.2346m, Kwd).Amount);
    }

    [Fact]
    public void TheDefaultRoundingIsBankersRounding()
    {
        // Half to even, so a long series of amounts does not drift upward the way half-up does.
        Assert.Equal(0.02m, Euros(0.025m).Amount);
        Assert.Equal(0.04m, Euros(0.035m).Amount);
    }

    [Fact]
    public void TheRoundingModeCanBeStatedAtConstruction() =>
        Assert.Equal(0.03m, Money.Of(0.025m, Eur, MidpointRounding.AwayFromZero).Amount);

    [Fact]
    public void ZeroCarriesACurrency()
    {
        Money zero = Money.Zero(Eur);

        Assert.True(zero.IsZero);
        Assert.Equal(Eur, zero.Currency);

        // There is no currency-less zero on purpose: it would be the one value allowed to be added to
        // anything, which is exactly the silent coercion this type exists to prevent.
        Assert.Throws<CurrencyMismatchException>(() => Money.Zero(Usd).Add(zero));
    }

    [Fact]
    public void AnAmountRoundTripsThroughMinorUnits()
    {
        Assert.Equal(19.99m, Money.FromMinorUnits(1999, Eur).Amount);
        Assert.Equal(1999, Euros(19.99m).ToMinorUnits());

        // Currencies without hundredths are the case a hard-coded 100 gets wrong.
        Assert.Equal(1000m, Money.FromMinorUnits(1000, Jpy).Amount);
        Assert.Equal(1500, Money.Of(1.5m, Kwd).ToMinorUnits());
    }

    [Fact]
    public void ADefaultMoneyRefusesToBeUsed()
    {
        Money uninitialized = default;

        // A struct field initialises to this without any constructor running, and "no currency" must
        // not behave like a currency.
        Assert.Throws<InvalidOperationException>(() => uninitialized.Add(Euros(1m)));
        Assert.Throws<InvalidOperationException>(() => uninitialized.Multiply(2m, RoundingPolicy.HalfEven));
        Assert.Equal("(uninitialized Money)", uninitialized.ToString());
    }

    // ── Currency checking ────────────────────────────────────────────────────────

    [Fact]
    public void AddingTwoCurrenciesIsRefused()
    {
        CurrencyMismatchException error = Assert.Throws<CurrencyMismatchException>(
            () => Euros(10m).Add(Money.Of(10m, Usd)));

        Assert.IsAssignableFrom<DomainException>(error);
        Assert.Equal("EUR", error.Left);
        Assert.Equal("USD", error.Right);
        Assert.Contains("ConvertAsync", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubtractingAndComparingAcrossCurrenciesIsRefusedToo()
    {
        Money euros = Euros(10m);
        Money dollars = Money.Of(10m, Usd);

        Assert.Throws<CurrencyMismatchException>(() => euros.Subtract(dollars));
        Assert.Throws<CurrencyMismatchException>(() => euros.CompareTo(dollars));
        Assert.Throws<CurrencyMismatchException>(() => euros < dollars);
    }

    [Fact]
    public void EqualityAcrossCurrenciesIsFalseRatherThanAnError() =>
        // Equality is not arithmetic: asking "are these the same amount of money" about €10 and $10 has
        // a correct answer, and it is no. Throwing here would break dictionaries and collections.
        Assert.NotEqual(Euros(10m), Money.Of(10m, Usd));

    // ── Arithmetic ───────────────────────────────────────────────────────────────

    [Fact]
    public void AddAndSubtractCombineAmountsInTheSameCurrency()
    {
        Assert.Equal(Euros(30.75m), Euros(10.25m).Add(Euros(20.50m)));
        Assert.Equal(Euros(-10.25m), Euros(10.25m).Subtract(Euros(20.50m)));
    }

    [Fact]
    public void MultiplyRoundsUnderTheStatedPolicy()
    {
        // €50.00 prorated over 13 of 30 days: 21.6666… — who absorbs the fraction is the call site's
        // decision, which is why the policy has no default.
        Money prorated = Euros(50m).Multiply(13m / 30m, RoundingPolicy.HalfEven);
        Assert.Equal(21.67m, prorated.Amount);

        Assert.Equal(21.66m, Euros(50m).Multiply(13m / 30m, RoundingPolicy.Down).Amount);
        Assert.Equal(21.67m, Euros(50m).Multiply(13m / 30m, RoundingPolicy.Up).Amount);
    }

    [Theory]
    [InlineData(0.10, 1.05, 0.10, 0.11)]    // 0.1050 → half-even keeps 0.10 (even), half-up gives 0.11
    [InlineData(0.30, 1.05, 0.32, 0.32)]    // 0.3150 → both agree: 0.32 is the even neighbour
    [InlineData(0.50, 1.05, 0.52, 0.53)]    // 0.5250 → half-even keeps 0.52 (even), half-up gives 0.53
    public void HalfEvenAndHalfUpDifferAtAnExactMidpoint(
        decimal amount,
        decimal factor,
        decimal expectedHalfEven,
        decimal expectedHalfUp)
    {
        // Whether these differ decides who absorbs half a cent, repeatedly, across every invoice line
        // in the system — which is why the policy is an argument and not a constant.
        Money money = Euros(amount);

        Assert.Equal(expectedHalfEven, money.Multiply(factor, RoundingPolicy.HalfEven).Amount);
        Assert.Equal(expectedHalfUp, money.Multiply(factor, RoundingPolicy.HalfUp).Amount);
    }

    [Fact]
    public void NegateAndAbsFlipAndDropTheSign()
    {
        Assert.Equal(Euros(-10m), Euros(10m).Negate());
        Assert.Equal(Euros(10m), Euros(-10m).Abs());
        Assert.True(Euros(-0.01m).IsNegative);
        Assert.True(Euros(0.01m).IsPositive);
    }

    [Fact]
    public void ComparisonReadsAsComparison()
    {
        Assert.True(Euros(10m) < Euros(20m));
        Assert.True(Euros(20m) >= Euros(20m));
        Assert.False(Euros(20m) > Euros(20m));
    }

    // ── Allocation: the invariant that matters ───────────────────────────────────

    [Fact]
    public void SplittingOneHundredThreeWaysLosesNoCent()
    {
        IReadOnlyList<Money> parts = Euros(100m).Allocate(1, 1, 1);

        // The canonical example from AD-42.
        Assert.Equal([Euros(33.34m), Euros(33.33m), Euros(33.33m)], parts);
    }

    [Fact]
    public void AllocationFollowsTheRatios()
    {
        Assert.Equal([Euros(75m), Euros(25m)], Euros(100m).Allocate(3, 1));
        Assert.Equal([Euros(60m), Euros(40m)], Euros(100m).Allocate(6, 4));
    }

    [Fact]
    public void AZeroRatioReceivesNothingButStillAppears()
    {
        // The shape of the result matches the shape of the request, so a caller can zip parts back
        // onto the lines they came from without tracking which were skipped.
        Assert.Equal([Euros(50m), Money.Zero(Eur), Euros(50m)], Euros(100m).Allocate(1, 0, 1));
    }

    [Theory]
    [InlineData(0.01, 3)]
    [InlineData(0.02, 3)]
    [InlineData(100, 3)]
    [InlineData(100, 7)]
    [InlineData(0.05, 100)]
    [InlineData(1234.56, 13)]
    [InlineData(-100, 3)]
    [InlineData(-0.01, 5)]
    [InlineData(0, 4)]
    public void TheSumOfEveryAllocationEqualsTheWhole(decimal amount, int parts)
    {
        // The property AD-42 asks for by name. Stated over a spread of awkward cases: amounts smaller
        // than the number of parts, negatives, zero, and splits that do not divide evenly.
        Money whole = Euros(amount);

        IReadOnlyList<Money> allocated = whole.Allocate([.. Enumerable.Repeat(1, parts)]);

        Assert.Equal(parts, allocated.Count);
        Assert.Equal(whole, allocated.Aggregate(Money.Zero(Eur), (running, part) => running.Add(part)));
    }

    [Fact]
    public void TheSumHoldsForLopsidedRatiosToo()
    {
        int[] ratios = [17, 3, 1, 41, 8];
        Money whole = Euros(999.99m);

        IReadOnlyList<Money> parts = whole.Allocate(ratios);

        Assert.Equal(whole, parts.Aggregate(Money.Zero(Eur), (running, part) => running.Add(part)));
    }

    [Fact]
    public void AnAmountSmallerThanThePartsGoesToTheEarliestParts()
    {
        // One cent among three: somebody gets it, and it is deterministic which — an invoice
        // recalculated tomorrow must match the one sent today.
        Assert.Equal([Euros(0.01m), Money.Zero(Eur), Money.Zero(Eur)], Euros(0.01m).Allocate(1, 1, 1));
    }

    [Fact]
    public void ACreditSplitsExactlyLikeTheChargeItReverses()
    {
        IReadOnlyList<Money> charge = Euros(100m).Allocate(1, 1, 1);
        IReadOnlyList<Money> credit = Euros(-100m).Allocate(1, 1, 1);

        // Otherwise the spare cent lands on a different party than it was charged to, and the two
        // never net to zero per line.
        Assert.Equal(charge.Select(part => part.Negate()), credit);
    }

    [Fact]
    public void AllocationRespectsCurrenciesWithoutHundredths()
    {
        IReadOnlyList<Money> parts = Money.Of(1000m, Jpy).Allocate(1, 1, 1);

        // No fractional yen exists, so the split must be 334/333/333 rather than 333.33 each.
        Assert.Equal(
            [Money.Of(334m, Jpy), Money.Of(333m, Jpy), Money.Of(333m, Jpy)],
            parts);
    }

    [Fact]
    public void AllocationRespectsCurrenciesWithThousandths()
    {
        IReadOnlyList<Money> parts = Money.Of(1m, Kwd).Allocate(1, 1, 1);

        Assert.Equal(
            [Money.Of(0.334m, Kwd), Money.Of(0.333m, Kwd), Money.Of(0.333m, Kwd)],
            parts);
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 0, 0 })]
    [InlineData(new[] { 1, -1 })]
    public void AnUnusableSetOfRatiosIsRejected(int[] ratios) =>
        Assert.Throws<ArgumentException>(() => Euros(100m).Allocate(ratios));

    // ── Presentation ─────────────────────────────────────────────────────────────

    [Fact]
    public void ToStringIsDiagnosticAndCultureInvariant()
    {
        // Deliberately not user-facing: the reader's culture decides separators, and that needs
        // ICurrencyService. This form is what a log line and a failing assertion show.
        Assert.Equal("19.99 EUR", Euros(19.99m).ToString());
        Assert.Equal("1000 JPY", Money.Of(1000m, Jpy).ToString());
        Assert.Equal("1.500 KWD", Money.Of(1.5m, Kwd).ToString());
    }

    [Fact]
    public void TwoAmountsWithTheSameValueAndCurrencyAreEqual()
    {
        Assert.Equal(Euros(10m), Euros(10.00m));
        Assert.Equal(Euros(10m).GetHashCode(), Euros(10.00m).GetHashCode());
    }

    // ── Division ─────────────────────────────────────────────────────────────────

    [Fact]
    public void DivideRoundsUnderTheStatedPolicy()
    {
        // The reason this method exists at all. Normalising an annual price to a month by
        // multiplying by `1m / 12` is not the same calculation: the reciprocal is not representable,
        // so a product that should land exactly on a half-cent lands just below it, and half-even
        // then rounds *down*. 599.94 / 12 is exactly 49.995, which must round to 50.00.
        Assert.Equal(Euros(50.00m), Euros(599.94m).Divide(12m, RoundingPolicy.HalfEven));
        Assert.Equal(Euros(1.02m), Euros(12.18m).Divide(12m, RoundingPolicy.HalfEven));
        Assert.Equal(Euros(100.00m), Euros(1199.94m).Divide(12m, RoundingPolicy.HalfEven));
    }

    [Fact]
    public void DivideIsNotMultiplicationByAReciprocal()
    {
        // Stated as a test rather than a comment, because this is the defect that motivated the
        // method: the two expressions disagree by a cent on 1 two-decimal annual price in 24, and
        // the reciprocal is always the low one.
        Money divided = Euros(599.94m).Divide(12m, RoundingPolicy.HalfEven);
        Money viaReciprocal = Euros(599.94m).Multiply(1m / 12m, RoundingPolicy.HalfEven);

        Assert.Equal(50.00m, divided.Amount);
        Assert.Equal(49.99m, viaReciprocal.Amount);
        Assert.True(divided > viaReciprocal);
    }

    [Fact]
    public void DivideRespectsAZeroDecimalCurrency()
    {
        // JPY has no minor units, so the midpoint sits on a whole yen. 59,994 / 12 is exactly
        // 4,999.5; half-even takes it to the even 5,000. The reciprocal form yields 4,999.
        Assert.Equal(Money.Of(5_000m, Jpy), Money.Of(59_994m, Jpy).Divide(12m, RoundingPolicy.HalfEven));
    }

    [Fact]
    public void DividingByZeroIsRefused()
    {
        Assert.Throws<DivideByZeroException>(() => Euros(10m).Divide(0m, RoundingPolicy.HalfEven));
    }

    [Fact]
    public void DividingAnUninitializedMoneyIsRefused()
    {
        Money uninitialized = default;

        Assert.Throws<InvalidOperationException>(() => uninitialized.Divide(2m, RoundingPolicy.HalfEven));
    }

}
