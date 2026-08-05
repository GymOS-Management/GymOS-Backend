namespace GymOS.SharedKernel.Currencies;

/// <summary>
/// How a monetary calculation resolves the fraction of a minor unit it produced
/// (ARCHITECTURE-ADDENDUM.md §3.1: "every rounding step is an explicit policy argument, because
/// implicit rounding is where billing bugs live").
///
/// <para>A named type rather than a bare <see cref="MidpointRounding"/> so call sites read as the
/// commercial decision they are — <c>RoundingPolicy.InTheCustomersFavour</c> says what
/// <c>MidpointRounding.ToNegativeInfinity</c> only implies — and so a future policy that
/// <see cref="MidpointRounding"/> cannot express (rounding to the nearest 5 cents, as Swiss cash
/// settlement does) can be added without changing every signature.</para>
/// </summary>
public readonly record struct RoundingPolicy
{
    private RoundingPolicy(MidpointRounding midpoint) => Midpoint = midpoint;

    /// <summary>
    /// Half to even — "banker's rounding". The default everywhere a *series* of amounts is rounded,
    /// because it does not drift upward the way half-up does over many lines.
    /// </summary>
    public static RoundingPolicy HalfEven => new(MidpointRounding.ToEven);

    /// <summary>
    /// Half away from zero — the "round half up" people expect when they check one figure by hand.
    /// Use where a human will verify a single amount against a receipt.
    /// </summary>
    public static RoundingPolicy HalfUp => new(MidpointRounding.AwayFromZero);

    /// <summary>
    /// Always toward zero (truncate). Never charges more than the exact amount, and never credits
    /// more than it should either — the safe direction when the sign of the amount is not known.
    /// </summary>
    public static RoundingPolicy TowardZero => new(MidpointRounding.ToZero);

    /// <summary>
    /// Always down. On a charge this favours the customer; on a credit it favours the gym — which is
    /// why the name says the direction rather than who benefits.
    /// </summary>
    public static RoundingPolicy Down => new(MidpointRounding.ToNegativeInfinity);

    /// <summary>Always up.</summary>
    public static RoundingPolicy Up => new(MidpointRounding.ToPositiveInfinity);

    /// <summary>The BCL rounding mode this policy applies.</summary>
    public MidpointRounding Midpoint { get; }

    public override string ToString() => Midpoint.ToString();
}
