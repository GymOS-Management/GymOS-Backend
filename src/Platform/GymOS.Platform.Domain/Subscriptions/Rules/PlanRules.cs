using System.Text.RegularExpressions;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Subscriptions.Rules;

/// <summary>
/// Value-level rules for a subscription plan, as rule objects with stable codes.
/// </summary>
public static partial class PlanRules
{
    /// <summary>Upper-case letters, digits, and single underscores — an identifier, not prose.</summary>
    [GeneratedRegex("^[A-Z0-9]+(_[A-Z0-9]+)*$", RegexOptions.CultureInvariant)]
    internal static partial Regex CodePattern();
}

/// <summary>
/// A plan's code: the stable handle integrations, exports, and support conversations use.
///
/// <para>Constrained to an upper-case identifier (<c>STARTER</c>, <c>PRO_ANNUAL</c>) rather than free
/// text, because it ends up in places that cannot cope with spaces or punctuation — a CSV export, a
/// URL, an accounting system's product field. The human-readable label is <c>Name</c>, which is free to
/// be anything.</para>
/// </summary>
/// <param name="PlanCode">
/// Named <c>PlanCode</c> rather than <c>Code</c> to leave <see cref="IBusinessRule.Code"/> — the rule's
/// own machine code — unshadowed. Two different meanings of "code" on one type is a trap.
/// </param>
public sealed record PlanCodeMustBeAnIdentifier(string? PlanCode) : IBusinessRule
{
    public const int MinimumLength = 2;
    public const int MaximumLength = 40;

    public string Code => "plans.code_invalid";

    public string Message =>
        $"'{PlanCode}' is not a valid plan code. Use {MinimumLength}–{MaximumLength} characters: " +
        "upper-case letters, digits, and single underscores, e.g. 'PRO' or 'PRO_ANNUAL'.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(PlanCode) ||
        PlanCode.Length < MinimumLength ||
        PlanCode.Length > MaximumLength ||
        !PlanRules.CodePattern().IsMatch(PlanCode);
}

public sealed record PlanNameIsRequired(string? Name) : IBusinessRule
{
    public const int MaximumLength = 120;

    public string Code => "plans.name_invalid";

    public string Message => $"A plan needs a name of at most {MaximumLength} characters.";

    public bool IsBroken() => string.IsNullOrWhiteSpace(Name) || Name.Length > MaximumLength;
}

/// <summary>
/// A trial length in whole days. Zero means no trial, which is different from a null nobody set.
/// </summary>
public sealed record TrialDaysMustBeSensible(int TrialDays) : IBusinessRule
{
    public const int Maximum = 365;

    public string Code => "plans.trial_days_invalid";

    public string Message => $"A trial must be between 0 and {Maximum} days, but was {TrialDays}.";

    public bool IsBroken() => TrialDays < 0 || TrialDays > Maximum;
}

/// <summary>
/// A plan limit: <see langword="null"/> is unlimited, and any stated ceiling must be positive.
///
/// <para>Null and zero are deliberately different. Null means "no ceiling"; zero would mean "none
/// allowed at all", which is not a plan anyone can sell. Modelling unlimited as 0 — or as
/// <c>int.MaxValue</c> — is how a limit check ends up reading backwards.</para>
/// </summary>
public sealed record PlanLimitMustBePositiveWhenSet(string Field, long? Limit) : IBusinessRule
{
    public string Code => "plans.limit_invalid";

    public string Message =>
        $"{Field} must be greater than zero when set, or left empty for unlimited. It was {Limit}.";

    public bool IsBroken() => Limit is <= 0;
}

/// <summary>
/// A price a gym could actually be charged: positive, and in a real currency.
///
/// <para>Zero is refused. A free plan is expressed by having no price row for a cycle, or by an
/// explicit free-tier decision — not by a €0.00 line on an invoice, which reads as a billing failure to
/// whoever receives it.</para>
/// </summary>
public sealed record PlanPriceMustBePositive(Money Amount) : IBusinessRule
{
    public string Code => "plans.price_invalid";

    public string Message => $"A price must be greater than zero, but was {Amount}.";

    public bool IsBroken() => !Amount.IsPositive;
}
