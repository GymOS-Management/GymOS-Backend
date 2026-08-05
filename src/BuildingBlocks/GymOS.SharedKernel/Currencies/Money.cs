using System.Globalization;

namespace GymOS.SharedKernel.Currencies;

/// <summary>
/// An amount of money in a specific currency (AD-42). Immutable, currency-checked, and rounded to the
/// currency's minor units on construction.
///
/// <para><b>Why this type exists.</b> Two bug classes disappear with it. "EUR added to USD compiling
/// silently" becomes a <see cref="CurrencyMismatchException"/>. "Cents created or lost in a split"
/// becomes impossible, because <see cref="Allocate"/> distributes remainders instead of dividing and
/// hoping. The third — implicit rounding — is addressed by making every rounding step name its policy:
/// <see cref="Multiply"/> has no default, deliberately, so nobody rounds money by accident.</para>
///
/// <para><b>Arithmetic is a method, not an operator</b>, for <c>Add</c>/<c>Subtract</c>/<c>Multiply</c>.
/// <c>a + b</c> reads as something that cannot fail; <c>a.Add(b)</c> reads as something with a rule
/// behind it, and there is one. Comparison operators <em>are</em> provided, because comparing two
/// amounts has no policy to choose and reads far worse as a method call.</para>
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>
    /// The amount, already rounded to <see cref="Currencies.Currency.MinorUnits"/>.
    ///
    /// <para>Exposed as a <see cref="decimal"/> for persistence and formatting only. Doing arithmetic
    /// on it is what this type exists to prevent, and rule R4 fails the build for money-typed
    /// <see cref="decimal"/> members outside this namespace.</para>
    /// </summary>
    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>Nothing, in a stated currency. There is no currency-less zero — see <see cref="Add"/>.</summary>
    public static Money Zero(Currency currency)
    {
        currency.EnsureSpecified();
        return new Money(0m, currency);
    }

    /// <summary>
    /// An amount in a currency, rounded to that currency's minor units.
    ///
    /// <para>Construction rounds because an amount that cannot be settled is not a price: €10.005 is
    /// not payable, and carrying it forward means the figure shown to a customer and the figure charged
    /// can differ by a cent nobody can account for. The default is banker's rounding, which does not
    /// drift upward across many amounts the way half-up does.</para>
    /// </summary>
    /// <exception cref="OverflowException">The amount is too large for <see cref="decimal"/>.</exception>
    public static Money Of(
        decimal amount,
        Currency currency,
        MidpointRounding rounding = MidpointRounding.ToEven)
    {
        currency.EnsureSpecified();
        return new Money(Math.Round(amount, currency.MinorUnits, rounding), currency);
    }

    /// <summary>An amount given in minor units: <c>FromMinorUnits(1999, Euro)</c> is €19.99.</summary>
    /// <remarks>
    /// The form payment providers and card terminals speak in, and exact by construction — no rounding
    /// decision arises, because a minor unit is already the smallest unit there is.
    /// </remarks>
    public static Money FromMinorUnits(long minorUnits, Currency currency)
    {
        currency.EnsureSpecified();
        return new Money(minorUnits / currency.MinorUnitFactor, currency);
    }

    /// <summary>This amount expressed in minor units: €19.99 is 1999.</summary>
    public long ToMinorUnits()
    {
        Currency.EnsureSpecified();

        // Amount is already rounded to the currency's minor units, so this scaling is exact and the
        // rounding argument only guards against a representation artefact.
        return (long)Math.Round(Amount * Currency.MinorUnitFactor, 0, MidpointRounding.ToEven);
    }

    /// <exception cref="CurrencyMismatchException">The two amounts are in different currencies.</exception>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <exception cref="CurrencyMismatchException">The two amounts are in different currencies.</exception>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>
    /// Scales the amount and rounds the result under <paramref name="policy"/> — used for proration,
    /// discounts, and per-unit pricing.
    /// </summary>
    /// <remarks>
    /// The policy has no default on purpose. Multiplying money almost always produces a fraction of a
    /// cent, and who absorbs it is a commercial decision the call site is in a position to make and
    /// this method is not.
    /// </remarks>
    public Money Multiply(decimal factor, RoundingPolicy policy)
    {
        Currency.EnsureSpecified();
        return new Money(Math.Round(Amount * factor, Currency.MinorUnits, policy.Midpoint), Currency);
    }

    public Money Negate()
    {
        Currency.EnsureSpecified();
        return new Money(-Amount, Currency);
    }

    /// <summary>The magnitude, dropping the sign — for presenting a credit as a positive figure.</summary>
    public Money Abs()
    {
        Currency.EnsureSpecified();
        return new Money(Math.Abs(Amount), Currency);
    }

    /// <summary>
    /// Splits this amount into parts proportional to <paramref name="ratios"/>, guaranteeing
    /// <c>sum(parts) == this</c>.
    ///
    /// <para>€100.00 in three equal parts yields 33.34 / 33.33 / 33.33 — not three amounts of 33.33
    /// with a cent unaccounted for. The work is done in whole minor units by the largest-remainder
    /// method: each part takes its floor share, and the leftover units go one each to the parts with
    /// the largest discarded fraction. Ties break toward the earlier ratio, so the distribution is
    /// deterministic and an invoice recalculated tomorrow matches the one sent today.</para>
    ///
    /// <para>Negative amounts (a refund being split) allocate by magnitude and are then negated, so a
    /// credit distributes exactly like the charge it reverses instead of dropping its spare cent on a
    /// different party.</para>
    /// </summary>
    /// <param name="ratios">
    /// Relative weights — <c>Allocate(1, 1, 1)</c> for an even three-way split, <c>Allocate(3, 1)</c>
    /// for 75/25. Zero weights are allowed and receive nothing; at least one must be positive.
    /// </param>
    /// <exception cref="ArgumentException">No ratios, a negative ratio, or every ratio zero.</exception>
    public IReadOnlyList<Money> Allocate(params int[] ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        Currency.EnsureSpecified();

        if (ratios.Length == 0)
        {
            throw new ArgumentException("At least one ratio is required.", nameof(ratios));
        }

        if (ratios.Any(ratio => ratio < 0))
        {
            throw new ArgumentException(
                "Ratios cannot be negative. To split a credit, allocate the amount and negate the " +
                "parts — Allocate already does that for a negative amount.",
                nameof(ratios));
        }

        long total = ratios.Sum(ratio => (long)ratio);
        if (total == 0)
        {
            throw new ArgumentException(
                "At least one ratio must be greater than zero; there is no way to divide an amount " +
                "among parts that all weigh nothing.",
                nameof(ratios));
        }

        long units = ToMinorUnits();
        int sign = units < 0 ? -1 : 1;
        long magnitude = Math.Abs(units);

        var parts = new long[ratios.Length];
        var remainders = new long[ratios.Length];
        long distributed = 0;

        for (int index = 0; index < ratios.Length; index++)
        {
            // checked: a ratio large enough to overflow here is a caller error, and a silently wrapped
            // product would produce a plausible-looking split that does not sum to the whole.
            long scaled = checked(magnitude * ratios[index]);
            parts[index] = scaled / total;
            remainders[index] = scaled % total;
            distributed += parts[index];
        }

        // Whatever floor division discarded, handed out one minor unit at a time.
        long leftover = magnitude - distributed;
        foreach (int index in Enumerable.Range(0, ratios.Length)
            .OrderByDescending(index => remainders[index])
            .ThenBy(index => index)
            .Take((int)leftover))
        {
            parts[index]++;
        }

        // Copied to a local because a lambda in a struct cannot capture `this`.
        Currency currency = Currency;
        return [.. parts.Select(part => FromMinorUnits(sign * part, currency))];
    }

    /// <exception cref="CurrencyMismatchException">The two amounts are in different currencies.</exception>
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Diagnostic form — <c>"19.99 EUR"</c>, invariant culture, never shown to a user.
    /// User-facing formatting needs the reader's culture and belongs to <c>ICurrencyService</c> (AD-43).
    /// </summary>
    public override string ToString() => Currency.IsSpecified
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"{Amount.ToString($"F{Currency.MinorUnits}", CultureInfo.InvariantCulture)} {Currency.Code}")
        : "(uninitialized Money)";

    private void EnsureSameCurrency(Money other)
    {
        Currency.EnsureSpecified();
        other.Currency.EnsureSpecified();

        if (Currency != other.Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }
}
