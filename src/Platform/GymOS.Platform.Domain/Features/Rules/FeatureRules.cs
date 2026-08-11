using System.Text.RegularExpressions;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Features.Rules;

/// <summary>
/// A feature key is a lower-case snake_case identifier — the form §2.4's twelve keys already take,
/// and the form a code constant and a JSON field can both carry unchanged.
/// </summary>
public sealed partial record FeatureKeyMustBeAnIdentifier(string? Key) : IBusinessRule
{
    public const int MaximumLength = 60;

    public string Code => "features.key_invalid";

    public string Message =>
        "A feature key is lower-case letters, digits, and underscores — for example `workout_builder` " +
        $"— and at most {MaximumLength} characters.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(Key)
        || Key.Trim().Length > MaximumLength
        || !KeyPattern().IsMatch(Key.Trim());

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>The catalogue is read by humans, so every row needs a label.</summary>
public sealed record FeatureNameIsRequired(string? Name) : IBusinessRule
{
    public const int MaximumLength = 120;

    public string Code => "features.name_invalid";

    public string Message => $"A feature needs a name of at most {MaximumLength} characters.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > MaximumLength;
}

/// <summary>
/// An override needs a stated reason.
///
/// <para>§2.4 lists the column; requiring it is this module's decision. An override is an exception
/// to what the gym pays for — a paid feature switched on for free, or an included one switched off —
/// and an exception with no reason is indistinguishable from a mistake by the time anyone asks. The
/// author and the timestamp are recorded automatically; only the "why" needs a human.</para>
/// </summary>
public sealed record OverrideReasonIsRequired(string? Reason) : IBusinessRule
{
    public const int MaximumLength = 500;

    /// <summary>
    /// The code as a constant as well as a property: the handler refuses a reasonless request before
    /// it touches the aggregate, and it must report the same code the aggregate would have thrown.
    /// </summary>
    public const string RuleCode = "features.override_reason_required";

    public string Code => RuleCode;

    public string Message =>
        "An override needs a reason — it is an exception to what the gym's plan gives, and a later " +
        $"reader has nothing else to go on. At most {MaximumLength} characters.";

    public bool IsBroken() =>
        string.IsNullOrWhiteSpace(Reason) || Reason.Trim().Length > MaximumLength;
}
