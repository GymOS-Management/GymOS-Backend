using GymOS.Platform.Application.Identity;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// The HTTP contract of <c>/api/v1/auth</c> (AD-12: this shape is what the generated
/// client types are built from, so it is API surface, not an implementation detail).
///
/// These records exist separately from the Application-layer results on purpose: a
/// version-specific wire shape is what lets v2 of an endpoint reuse the same handler
/// (AD-37). Note what is absent — no response type carries the refresh token; it goes
/// out only as an httpOnly cookie.
/// </summary>
public sealed record SignInRequest(string Email, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

/// <param name="AccessToken">Bearer token for the <c>Authorization</c> header.</param>
/// <param name="TokenType">Always <c>Bearer</c>; present so clients need not hardcode it.</param>
/// <param name="ExpiresInSeconds">
/// Lifetime remaining, the OAuth 2.0-shaped field clients schedule their refresh from.
/// A relative value survives client clock skew, which an absolute one does not.
/// </param>
/// <param name="ExpiresAtUtc">The same instant absolutely, for logs and debugging.</param>
public sealed record AccessTokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    DateTimeOffset ExpiresAtUtc)
{
    public static AccessTokenResponse From(AuthenticatedSession session, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(session);

        return new AccessTokenResponse(
            session.AccessToken,
            "Bearer",
            (int)Math.Max(0, (session.AccessTokenExpiresAtUtc - now).TotalSeconds),
            session.AccessTokenExpiresAtUtc);
    }
}

/// <param name="Permissions">
/// The effective permission set the UI gates on (AD-22 §6.3). Advisory only —
/// hiding a button is not authorization; the server checks every call independently.
/// </param>
public sealed record CurrentUserResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);
