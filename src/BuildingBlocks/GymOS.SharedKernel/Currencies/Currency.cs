using System.Collections.Frozen;

namespace GymOS.SharedKernel.Currencies;

/// <summary>
/// An ISO 4217 currency: its alphabetic code and how many minor units it divides into
/// (ARCHITECTURE-ADDENDUM.md §3.1).
///
/// <para><b>Minor units are the whole point of this type.</b> Treating JPY as having cents, or KWD as
/// having two decimal places instead of three, creates or destroys money at every rounding step. The
/// code alone cannot tell you that, so it is carried alongside.</para>
///
/// <para>The symbol is deliberately absent: a currency's presentation depends on the *reader's*
/// locale, not on the currency ("$" is ambiguous across a dozen currencies, and €1.000,50 versus
/// €1,000.50 is a culture decision). Formatting belongs to <c>ICurrencyService</c> (AD-43), which has a
/// tenant culture to format against.</para>
/// </summary>
public readonly record struct Currency
{
    /// <summary>
    /// Currencies that do <b>not</b> divide into hundredths, by ISO 4217 as of 2026-08 (amendment 179).
    ///
    /// <para>An exception list rather than a full table of every currency: encoding all ~180 would be
    ///180 lines to maintain for one fact that is the same for all but these, and a stale entry in the
    /// long list is harder to spot than a missing one here. The default is 2, which is correct for
    /// every active currency absent from this map — asserted by <c>CurrencyTests</c> against the ISO
    /// non-two list, so adding a currency ISO defines differently means updating both together.</para>
    ///
    /// <para>Commodity codes (XAU gold, XAG silver, XPD, XPT) and XDR are here because they have no
    /// minor unit at all; they are not money a gym trades in, but rounding them to cents would be
    /// silently wrong if one ever appeared.</para>
    /// </summary>
    private static readonly FrozenDictionary<string, int> MinorUnitExceptions =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // No minor unit.
            ["BIF"] = 0,
            ["CLP"] = 0,
            ["DJF"] = 0,
            ["GNF"] = 0,
            ["ISK"] = 0,
            ["JPY"] = 0,
            ["KMF"] = 0,
            ["KRW"] = 0,
            ["PYG"] = 0,
            ["RWF"] = 0,
            ["UGX"] = 0,
            ["UYI"] = 0,
            ["VND"] = 0,
            ["VUV"] = 0,
            ["XAF"] = 0,
            ["XOF"] = 0,
            ["XPF"] = 0,
            ["XSU"] = 0,
            ["XUA"] = 0,
            ["XAU"] = 0,
            ["XAG"] = 0,
            ["XPD"] = 0,
            ["XPT"] = 0,
            ["XDR"] = 0,

            // Thousandths.
            ["BHD"] = 3,
            ["IQD"] = 3,
            ["JOD"] = 3,
            ["KWD"] = 3,
            ["LYD"] = 3,
            ["OMR"] = 3,
            ["TND"] = 3,

            // Ten-thousandths (unit-of-account currencies).
            ["CLF"] = 4,
            ["UYW"] = 4,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Minor units assumed for any code not in <see cref="MinorUnitExceptions"/>.</summary>
    public const int DefaultMinorUnits = 2;

    /// <summary>The largest minor-unit count ISO 4217 defines, and the storage precision it implies.</summary>
    public const int MaximumMinorUnits = 4;

    private readonly string? _code;

    private Currency(string code, int minorUnits)
    {
        _code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>The euro, spelled out because it is the currency GymOS was built for.</summary>
    public static Currency Euro => new("EUR", 2);

    /// <summary>ISO 4217 alphabetic code, upper case.</summary>
    /// <exception cref="InvalidOperationException">
    /// The currency is <see langword="default"/> — see <see cref="EnsureSpecified"/>.
    /// </exception>
    public string Code
    {
        get
        {
            EnsureSpecified();
            return _code!;
        }
    }

    /// <summary>
    /// How many decimal places this currency settles in: 2 for EUR, 0 for JPY, 3 for KWD.
    /// </summary>
    public int MinorUnits { get; }

    /// <summary>Whether this value came from <see cref="Of"/> rather than being <see langword="default"/>.</summary>
    public bool IsSpecified => _code is not null;

    /// <summary>
    /// The scaling factor between a major unit and a minor one: 100 for EUR, 1 for JPY, 1000 for KWD.
    /// </summary>
    public decimal MinorUnitFactor => MinorUnits switch
    {
        0 => 1m,
        1 => 10m,
        2 => 100m,
        3 => 1_000m,
        4 => 10_000m,
        _ => throw new InvalidOperationException($"Unsupported minor-unit count {MinorUnits}."),
    };

    /// <summary>
    /// Resolves a currency from its ISO 4217 alphabetic code, case-insensitively.
    ///
    /// <para>The code is validated for <em>shape</em> only — three ASCII letters — and not against a
    /// list of currencies that exist. A membership check would refuse a real currency the day ISO adds
    /// one, and the meaningful risk is not an invented code (nothing else in the platform would work
    /// with it either) but a <em>real</em> currency assigned the wrong number of decimal places, which
    /// is what <see cref="MinorUnitExceptions"/> exists to prevent.</para>
    /// </summary>
    /// <exception cref="ArgumentException">The code is not three ASCII letters.</exception>
    public static Currency Of(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        string normalized = code.Trim().ToUpperInvariant();

        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"'{code}' is not an ISO 4217 alphabetic currency code. Expected three letters, e.g. 'EUR'.",
                nameof(code));
        }

        return new Currency(
            normalized,
            MinorUnitExceptions.TryGetValue(normalized, out int minorUnits) ? minorUnits : DefaultMinorUnits);
    }

    public static bool TryOf(string? code, out Currency currency)
    {
        try
        {
            currency = Of(code!);
            return true;
        }
        catch (ArgumentException)
        {
            currency = default;
            return false;
        }
    }

    public override string ToString() => _code ?? "(unspecified currency)";

    /// <summary>
    /// Guards against a <see langword="default"/> currency, which a struct field or an uninitialized
    /// collection slot can produce without any constructor running. Failing loudly here beats
    /// arithmetic that silently treats "no currency" as a currency.
    /// </summary>
    internal void EnsureSpecified()
    {
        if (_code is null)
        {
            throw new InvalidOperationException(
                "This Currency is uninitialized (default). Build one with Currency.Of(\"EUR\") — a " +
                "default currency has no code and no minor-unit count, so no amount in it can be " +
                "rounded or compared.");
        }
    }
}
