using GymOS.SharedKernel.Rules;

namespace GymOS.TestingSupport;

/// <summary>
/// Assertions for <see cref="IBusinessRule"/> implementations, so rule tests read as rules rather
/// than as boolean plumbing.
///
/// Framework-agnostic (it throws <see cref="BusinessRuleAssertionException"/> rather than depending
/// on xunit) because <c>GymOS.TestingSupport</c> is shared by every test project and must not pick a
/// test framework for them.
/// </summary>
public static class BusinessRuleAssert
{
    /// <summary>Asserts the rule is broken, and that its code matches when one is given.</summary>
    public static void Broken(IBusinessRule rule, string? expectedCode = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.IsBroken())
        {
            throw new BusinessRuleAssertionException(
                $"Expected rule '{rule.Code}' to be broken, but it was satisfied.");
        }

        if (expectedCode is not null && !string.Equals(rule.Code, expectedCode, StringComparison.Ordinal))
        {
            throw new BusinessRuleAssertionException(
                $"Expected code '{expectedCode}' but the rule reported '{rule.Code}'.");
        }

        // A rule that fails without saying why produces an error message nobody can act on, so the
        // message is part of the contract and worth asserting alongside the verdict.
        if (string.IsNullOrWhiteSpace(rule.Message))
        {
            throw new BusinessRuleAssertionException(
                $"Rule '{rule.Code}' is broken but carries no message.");
        }
    }

    public static void Satisfied(IBusinessRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.IsBroken())
        {
            throw new BusinessRuleAssertionException(
                $"Expected rule '{rule.Code}' to be satisfied, but it was broken: {rule.Message}");
        }
    }

    /// <summary>
    /// Asserts every rule carries a dotted, lower-case code — the convention clients branch on.
    /// Point a module's whole rule catalog at this and the naming cannot drift one rule at a time.
    /// </summary>
    public static void CodesFollowConvention(params IBusinessRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (IBusinessRule rule in rules)
        {
            string code = rule.Code;

            bool wellFormed =
                !string.IsNullOrWhiteSpace(code) &&
                code.Contains('.', StringComparison.Ordinal) &&
                code.Equals(code.ToLowerInvariant(), StringComparison.Ordinal) &&
                !code.StartsWith('.') &&
                !code.EndsWith('.') &&
                !code.Any(char.IsWhiteSpace);

            if (!wellFormed)
            {
                throw new BusinessRuleAssertionException(
                    $"Rule code '{code}' does not follow the '<area>.<snake_case_reason>' convention.");
            }
        }
    }
}

public sealed class BusinessRuleAssertionException : Exception
{
    public BusinessRuleAssertionException()
    {
    }

    public BusinessRuleAssertionException(string message)
        : base(message)
    {
    }

    public BusinessRuleAssertionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
