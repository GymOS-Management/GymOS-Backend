namespace GymOS.Platform.Application.Identity;

/// <summary>
/// What a successful sign-in or refresh yields (AD-4). The two tokens travel by
/// different routes and that asymmetry is the design: the access token goes in the
/// response body for the client to attach as a bearer header, the refresh token goes
/// into an httpOnly cookie the client's JavaScript can never read.
/// </summary>
/// <param name="AccessToken">Short-lived JWT (15 minutes by default).</param>
/// <param name="AccessTokenExpiresAtUtc">Absolute expiry of the access token.</param>
/// <param name="RefreshToken">
/// The raw opaque refresh token. The only moment it exists outside the client:
/// the endpoint writes it to the cookie and it is never logged, returned in a body,
/// or stored — the database holds its hash (AD-4).
/// </param>
/// <param name="RefreshTokenExpiresAtUtc">Absolute expiry of the refresh token, for the cookie.</param>
public sealed record AuthenticatedSession(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAtUtc,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAtUtc);
