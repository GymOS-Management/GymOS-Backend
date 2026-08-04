using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity;

/// <summary>
/// The one place a password is judged acceptable — every write path (change-password,
/// the SuperAdmin seed, future invitation and reset flows) goes through it, so the
/// rule cannot drift between them.
///
/// Length is the requirement, not composition classes: NIST SP 800-63B deprecated
/// mandatory character-class rules precisely because they push users toward
/// predictable substitutions. Breach-corpus checks belong to a later milestone —
/// that is a service call, not a rule.
/// </summary>
public static class PasswordPolicy
{
    public static Result Validate(string? password, int minimumLength)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return Result.Failure(AuthenticationErrors.WeakPassword("A password is required."));
        }

        if (password.Length < minimumLength)
        {
            return Result.Failure(AuthenticationErrors.WeakPassword(
                $"The password must be at least {minimumLength} characters long."));
        }

        // Leading/trailing whitespace survives a copy-paste and then fails at the next
        // sign-in with no clue why. Rejecting it is kinder than silently trimming it.
        if (password.Length != password.Trim().Length)
        {
            return Result.Failure(AuthenticationErrors.WeakPassword(
                "The password cannot start or end with whitespace."));
        }

        return Result.Success();
    }
}
