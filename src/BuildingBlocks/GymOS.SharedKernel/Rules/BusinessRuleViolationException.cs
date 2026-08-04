namespace GymOS.SharedKernel.Rules;

/// <summary>
/// A business rule was broken.
///
/// <para>Derives from <see cref="DomainException"/> so it fits the existing strategy rather than
/// adding a second one: business failures are <c>Result</c> values, and an exception from the domain
/// means an invariant was breached — i.e. the caller sent state the domain forbids. Handlers that
/// want a typed response catch this and translate it, which is how the API returns a 400 with the
/// rule's code instead of a 500.</para>
///
/// <para>It carries the <see cref="IBusinessRule"/> itself, not just a message, so the translation
/// has the stable code available and does not have to parse prose.</para>
/// </summary>
public sealed class BusinessRuleViolationException : DomainException
{
    public BusinessRuleViolationException(IBusinessRule brokenRule)
        : base(FormatMessage(brokenRule))
    {
        ArgumentNullException.ThrowIfNull(brokenRule);
        BrokenRule = brokenRule;
        Code = brokenRule.Code;
    }

    public BusinessRuleViolationException()
    {
    }

    public BusinessRuleViolationException(string message)
        : base(message)
    {
    }

    public BusinessRuleViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The rule that failed, for callers that need more than the message.</summary>
    public IBusinessRule? BrokenRule { get; }

    /// <summary>The broken rule's stable code, or <see langword="null"/> when constructed without one.</summary>
    public string? Code { get; }

    private static string FormatMessage(IBusinessRule brokenRule)
    {
        ArgumentNullException.ThrowIfNull(brokenRule);

        // The code is in the message as well as the property: exception text is what ends up in a
        // log, and a log line naming the rule is worth far more than one that only describes it.
        return $"{brokenRule.Code}: {brokenRule.Message}";
    }
}
