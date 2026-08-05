namespace GymOS.SharedKernel.Currencies;

/// <summary>
/// Two amounts in different currencies were combined (AD-42: "throws on EUR + USD — never silent
/// coercion").
///
/// <para>A <see cref="DomainException"/> rather than a <c>Result</c> failure: no caller can
/// recover from it and no user can be asked to fix it. Adding euros to dollars is a defect in the code
/// that did it, and the conversion it needed is <c>ICurrencyService.ConvertAsync</c> (AD-43) — which
/// records the rate it used, because a converted figure nobody can explain later is worse than an
/// error today.</para>
/// </summary>
public sealed class CurrencyMismatchException : DomainException
{
    public CurrencyMismatchException(Currency left, Currency right)
        : base(
            $"Cannot combine {left} and {right} amounts. Convert one first with " +
            "ICurrencyService.ConvertAsync, which records the rate, provider, and as-of date used.")
    {
        Left = left.ToString();
        Right = right.ToString();
    }

    public CurrencyMismatchException()
        : base("Cannot combine amounts in two different currencies.")
    {
    }

    public CurrencyMismatchException(string message)
        : base(message)
    {
    }

    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? Left { get; }

    public string? Right { get; }
}
