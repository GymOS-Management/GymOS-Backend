using GymOS.SharedKernel.Results;

namespace GymOS.SharedKernel.Rules;

/// <summary>
/// How aggregates check rules (<see cref="IBusinessRule"/>).
///
/// <para>Two shapes, because the domain and the application need different answers to the same
/// question. Inside an aggregate a broken rule is an invariant breach and throwing is correct — the
/// aggregate must not exist in that state. At the application boundary a broken rule is a business
/// outcome the caller should be told about, so it becomes a <c>Result</c>. Both read the same rule
/// object, which is the point: one definition, two ways of reporting it.</para>
/// </summary>
public static class BusinessRules
{
    /// <summary>
    /// Throws <see cref="BusinessRuleViolationException"/> if the rule is broken. For use inside
    /// aggregates and value objects, where an invalid state must not be constructible.
    /// </summary>
    public static void Check(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBroken())
        {
            throw new BusinessRuleViolationException(rule);
        }
    }

    /// <summary>
    /// Checks several rules and throws on the first broken one.
    ///
    /// First-failure rather than all-failures on purpose: this path guards an invariant, and once
    /// one is broken the object is not being created, so collecting the rest is work nobody reads.
    /// Use <see cref="Validate(IBusinessRule[])"/> when a caller deserves the full list.
    /// </summary>
    public static void CheckAll(params IBusinessRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (IBusinessRule rule in rules)
        {
            Check(rule);
        }
    }

    /// <summary>
    /// Evaluates a rule as a business outcome instead of an exception, for handlers deciding what to
    /// tell the caller.
    /// </summary>
    public static Result Validate(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule.IsBroken()
            ? Result.Failure(Error.Validation(rule.Code, rule.Message))
            : Result.Success();
    }

    /// <summary>
    /// Evaluates several rules and reports the first violation.
    ///
    /// <para>Returns one error rather than a list because <c>Result</c> carries one <see cref="Error"/>;
    /// a multi-error response shape (RFC 7807 with per-field errors, ARCHITECTURE.md §3) is the
    /// validation pipeline's job, not this helper's. When per-field reporting matters, that is a
    /// FluentValidation validator, not a business rule.</para>
    /// </summary>
    public static Result Validate(params IBusinessRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (IBusinessRule rule in rules)
        {
            Result result = Validate(rule);

            if (result.IsFailure)
            {
                return result;
            }
        }

        return Result.Success();
    }

    /// <summary>Whether a rule holds, without throwing or allocating a result — for conditionals.</summary>
    public static bool IsSatisfied(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return !rule.IsBroken();
    }
}
