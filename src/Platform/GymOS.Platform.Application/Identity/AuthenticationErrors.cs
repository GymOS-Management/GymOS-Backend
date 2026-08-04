using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity;

/// <summary>
/// The authentication failure vocabulary. Codes are stable API — clients branch on
/// them, so they are never reworded away.
///
/// Deliberate coarseness: a wrong password, an unknown email, and an un-activated
/// invitation all return <see cref="InvalidCredentials"/>. Distinguishing them would
/// turn the login endpoint into an account-existence oracle.
/// </summary>
public static class AuthenticationErrors
{
    public static Error InvalidCredentials => Error.Unauthorized(
        "auth.invalid_credentials",
        "The email or password is incorrect.");

    /// <summary>
    /// Lockout is reported distinctly and on purpose: the account provably exists
    /// (it got locked), and the legitimate owner needs to know why waiting is the
    /// answer rather than retrying. The trade-off is accepted, not overlooked.
    /// </summary>
    public static Error AccountLocked => Error.Unauthorized(
        "auth.account_locked",
        "Too many failed sign-in attempts. Try again later.");

    public static Error AccountSuspended => Error.Forbidden(
        "auth.account_suspended",
        "This account is suspended. Contact a platform administrator.");

    /// <summary>Missing, unknown, expired, or already-rotated refresh token — one code for all four.</summary>
    public static Error InvalidRefreshToken => Error.Unauthorized(
        "auth.invalid_refresh_token",
        "The session is no longer valid. Sign in again.");

    public static Error InvalidCurrentPassword => Error.Unauthorized(
        "auth.invalid_current_password",
        "The current password is incorrect.");

    public static Error UserNotFound => Error.NotFound(
        "auth.user_not_found",
        "The user no longer exists.");

    public static Error WeakPassword(string reason) => Error.Validation("auth.weak_password", reason);
}
