namespace GymOS.SharedKernel;

/// <summary>
/// Thrown when a domain invariant would be violated (e.g. an illegal status
/// transition). Distinct from business failures, which are <c>Result</c> values:
/// a <see cref="DomainException"/> reaching a caller indicates a programming or
/// flow error, not a user-facing outcome.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException()
    {
    }

    public DomainException(string message)
        : base(message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
