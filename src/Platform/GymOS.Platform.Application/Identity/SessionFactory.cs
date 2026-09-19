using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity;

/// <summary>
/// Issues one authenticated session: an access token asserting the user's current
/// permissions plus a fresh refresh token recorded by hash. Shared by sign-in and
/// refresh so both produce byte-identical session semantics — a rotated session is
/// exactly as capable as a freshly signed-in one, never accidentally more or less.
///
/// It does not save: the caller owns the transaction boundary, so token issuance
/// commits together with the state change that justified it (AD-13).
/// </summary>
internal sealed class SessionFactory
{
    private readonly IIdentityQueries _identityQueries;
    private readonly IAccessTokenIssuer _accessTokenIssuer;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly ISettings<LoginPolicyOptions> _policy;

    public SessionFactory(
        IIdentityQueries identityQueries,
        IAccessTokenIssuer accessTokenIssuer,
        IRefreshTokenGenerator refreshTokenGenerator,
        IRefreshTokenRepository refreshTokens,
        ISettings<LoginPolicyOptions> policy)
    {
        _identityQueries = identityQueries;
        _accessTokenIssuer = accessTokenIssuer;
        _refreshTokenGenerator = refreshTokenGenerator;
        _refreshTokens = refreshTokens;
        _policy = policy;
    }

    /// <param name="user">The authenticated user.</param>
    /// <param name="ipAddress">Recorded on the refresh token for the audit trail.</param>
    /// <param name="now">Time as an input, never an ambient read (AD-40).</param>
    /// <param name="replacing">
    /// On rotation, the token being replaced: it is revoked here and linked to its
    /// successor's hash, which is what makes the chain — and therefore reuse
    /// detection — possible (AD-4).
    /// </param>
    public async Task<AuthenticatedSession> IssueAsync(
        User user,
        string ipAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        RefreshToken? replacing = null)
    {
        IReadOnlyCollection<string> permissions =
            await _identityQueries.GetEffectivePermissionsAsync(user.Id, cancellationToken);

        IssuedAccessToken accessToken = _accessTokenIssuer.Issue(new AccessTokenRequest(
            user.Id,
            user.Email,
            user.SecurityStamp,
            permissions,
            now));

        string rawRefreshToken = _refreshTokenGenerator.GenerateRawToken();
        string refreshTokenHash = _refreshTokenGenerator.ComputeHash(rawRefreshToken);
        DateTimeOffset refreshExpiresAtUtc = now.Add(_policy.Current.RefreshTokenLifetime);

        replacing?.Revoke(now, ipAddress, RevocationReasons.Rotated, refreshTokenHash);
        _refreshTokens.Add(RefreshToken.Issue(user.Id, refreshTokenHash, refreshExpiresAtUtc, ipAddress));

        return new AuthenticatedSession(
            accessToken.Token,
            accessToken.ExpiresAtUtc,
            rawRefreshToken,
            refreshExpiresAtUtc);
    }
}

/// <summary>
/// Why a refresh token died. Stored verbatim on the row, so the audit trail can tell
/// an ordinary rotation apart from a security event years later.
/// </summary>
public static class RevocationReasons
{
    public const string Rotated = "rotated";
    public const string SignedOut = "signed_out";
    public const string ReuseDetected = "reuse_detected";
    public const string PasswordChanged = "password_changed";
    public const string AccountSuspended = "account_suspended";
}
