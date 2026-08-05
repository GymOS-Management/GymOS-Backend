using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.SharedKernel.UnitTests;

public sealed class CurrencyTests
{
    /// <summary>
    /// Every currency ISO 4217 does <b>not</b> divide into hundredths, as of amendment 179 (2026-08).
    ///
    /// <para>Duplicated from <c>Currency</c>'s own table on purpose: the point of the assertion is that
    /// two independently written lists agree. Copying the production dictionary into the test would
    /// assert only that a field equals itself.</para>
    /// </summary>
    public static TheoryData<string, int> NonDecimalCurrencies() => new()
    {
        { "BIF", 0 }, { "CLP", 0 }, { "DJF", 0 }, { "GNF", 0 }, { "ISK", 0 }, { "JPY", 0 },
        { "KMF", 0 }, { "KRW", 0 }, { "PYG", 0 }, { "RWF", 0 }, { "UGX", 0 }, { "UYI", 0 },
        { "VND", 0 }, { "VUV", 0 }, { "XAF", 0 }, { "XOF", 0 }, { "XPF", 0 }, { "XSU", 0 },
        { "XUA", 0 }, { "XAU", 0 }, { "XAG", 0 }, { "XPD", 0 }, { "XPT", 0 }, { "XDR", 0 },
        { "BHD", 3 }, { "IQD", 3 }, { "JOD", 3 }, { "KWD", 3 }, { "LYD", 3 }, { "OMR", 3 },
        { "TND", 3 },
        { "CLF", 4 }, { "UYW", 4 },
    };

    [Theory]
    [MemberData(nameof(NonDecimalCurrencies))]
    public void ACurrencyThatDoesNotDivideIntoHundredthsKnowsIt(string code, int expectedMinorUnits)
    {
        // Getting this wrong creates or destroys money at every rounding step: charging ¥1,000 as
        // ¥1,000.00 or KWD 1.500 as KWD 1.50 is a real, silent error.
        Currency currency = Currency.Of(code);

        Assert.Equal(expectedMinorUnits, currency.MinorUnits);
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("USD")]
    [InlineData("GBP")]
    [InlineData("CHF")]
    [InlineData("AUD")]
    public void AnOrdinaryCurrencyHasTwoMinorUnits(string code) =>
        Assert.Equal(2, Currency.Of(code).MinorUnits);

    [Fact]
    public void NoNonDecimalCurrencyIsMissingFromTheExceptionList()
    {
        // The failure this guards against: a currency with 0 or 3 decimals falling through to the
        // default of 2. Restated as a property so a future ISO amendment fails here rather than in a
        // customer's invoice.
        var declared = NonDecimalCurrencies()
            .Select(row => ((string)row[0]!, (int)row[1]!))
            .ToList();

        Assert.All(declared, entry =>
            Assert.NotEqual(Currency.DefaultMinorUnits, Currency.Of(entry.Item1).MinorUnits));
    }

    [Theory]
    [InlineData("eur", "EUR")]
    [InlineData(" EUR ", "EUR")]
    [InlineData("Eur", "EUR")]
    public void CodesAreNormalizedToUpperCase(string input, string expected) =>
        // Case-insensitive by standard; an operator typing "eur" is not making a mistake.
        Assert.Equal(expected, Currency.Of(input).Code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("EU")]
    [InlineData("EURO")]
    [InlineData("EU1")]
    [InlineData("€")]
    public void AMalformedCodeIsRejected(string? code) =>
        Assert.ThrowsAny<ArgumentException>(() => Currency.Of(code!));

    [Fact]
    public void AnUnlistedButWellFormedCodeIsAcceptedWithTheDefaultMinorUnits()
    {
        // Deliberate: shape is validated, membership is not. Refusing an unknown code would reject a
        // real currency the day ISO adds one, and an invented code fails everywhere else anyway.
        Currency currency = Currency.Of("QQQ");

        Assert.Equal("QQQ", currency.Code);
        Assert.Equal(Currency.DefaultMinorUnits, currency.MinorUnits);
    }

    [Fact]
    public void TryOfReportsFailureInsteadOfThrowing()
    {
        Assert.False(Currency.TryOf("nonsense", out Currency invalid));
        Assert.False(invalid.IsSpecified);

        Assert.True(Currency.TryOf("eur", out Currency valid));
        Assert.Equal(Currency.Euro, valid);
    }

    [Fact]
    public void TheEuroIsTheSameValueHoweverItIsObtained() =>
        // A record struct compares by value, so the named static and a parsed code are interchangeable.
        Assert.Equal(Currency.Euro, Currency.Of("EUR"));

    [Theory]
    [InlineData("JPY", 1)]
    [InlineData("EUR", 100)]
    [InlineData("KWD", 1000)]
    [InlineData("CLF", 10000)]
    public void TheMinorUnitFactorFollowsTheMinorUnitCount(string code, decimal expected) =>
        Assert.Equal(expected, Currency.Of(code).MinorUnitFactor);

    [Fact]
    public void ADefaultCurrencyRefusesToBeUsedRatherThanPretendingToBeOne()
    {
        Currency uninitialized = default;

        Assert.False(uninitialized.IsSpecified);

        // A struct field or an array slot produces this without any constructor running, and an
        // amount "in no currency" must not be quietly roundable.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => uninitialized.Code);
        Assert.Contains("Currency.Of", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnspecifiedCurrencyStillPrintsSomethingReadable() =>
        // ToString must never throw: it is what a logger and a debugger call.
        Assert.Equal("(unspecified currency)", default(Currency).ToString());
}
