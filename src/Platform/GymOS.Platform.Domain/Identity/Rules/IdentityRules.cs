using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Identity.Rules;

/// <summary>
/// The platform-identity rules, as rule objects (<see cref="IBusinessRule"/>).
///
/// Deliberately few: M1's identity invariants are mostly state transitions, which
/// <see cref="UserStatusTransitions"/> already expresses better than a rule object would. What
/// lives here is the value-level checks — the shape of an email address — where naming the rule and
/// testing it in isolation earns its keep. Feature modules will have many more.
/// </summary>
public sealed record EmailMustBeWellFormed(string? Email) : IBusinessRule
{
    public string Code => "identity.email_malformed";

    public string Message => $"'{Email}' is not a valid email address.";

    /// <summary>
    /// A deliberately shallow check: non-empty, exactly one <c>@</c>, something either side, and no
    /// whitespace.
    ///
    /// Not a full RFC 5322 grammar, because that accepts addresses no mail server will deliver to
    /// and rejects nothing users actually mistype. The real proof that an address exists is a
    /// delivered activation email, which is where the activation flow gets its confirmation from —
    /// this only catches the obviously wrong before it reaches the database.
    /// </summary>
    public bool IsBroken()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            return true;
        }

        string[] parts = Email.Split('@');

        return parts.Length != 2 ||
            parts[0].Length == 0 ||
            parts[1].Length == 0 ||
            !parts[1].Contains('.', StringComparison.Ordinal) ||
            Email.Any(char.IsWhiteSpace);
    }
}
