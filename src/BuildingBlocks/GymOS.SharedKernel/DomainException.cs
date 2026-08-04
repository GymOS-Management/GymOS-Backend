namespace GymOS.SharedKernel;

/// <summary>
/// Thrown when a domain invariant would be violated (e.g. an illegal status
/// transition). Distinct from business failures, which are <c>Result</c> values:
/// a <see cref="DomainException"/> reaching a caller indicates a programming or
/// flow error, not a user-facing outcome.
///
/// <para>Not sealed, so a broken business rule can be one of these
/// (<c>BusinessRuleViolationException</c>) rather than a parallel hierarchy. That keeps one
/// exception strategy: everything the domain refuses is a <see cref="DomainException"/>, and every
/// existing <c>catch (DomainException)</c> keeps working for rules added later. Derive only to add
/// information — never to change what it means.</para>
/// </summary>
public class DomainException : Exception
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
