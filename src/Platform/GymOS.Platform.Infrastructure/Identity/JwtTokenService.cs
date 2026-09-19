using System.Security.Cryptography;
using System.Text;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Issues the access tokens (AD-4). The only place a JWT type exists on the write
/// path — the Application layer sees <see cref="IssuedAccessToken"/> and nothing more
/// (rule R8), which is what keeps a future move to asymmetric keys or a different token
/// format a one-file change.
/// </summary>
internal sealed class JwtTokenService : IAccessTokenIssuer
{
    private readonly ISettings<JwtOptions> _settings;

    public JwtTokenService(ISettings<JwtOptions> settings) => _settings = settings;

    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        JwtOptions options = _settings.Current;

        // Second precision, floored: JWT time claims are integer seconds (RFC 7519),
        // so rounding up would hand out a token that is valid one tick before its own
        // `nbf`. The returned expiry is the token's actual one, not the requested one.
        DateTime issuedAtUtc = Floor(request.IssuedAtUtc);
        DateTime expiresAtUtc = issuedAtUtc.Add(options.AccessTokenLifetime);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = issuedAtUtc,
            NotBefore = issuedAtUtc,
            Expires = expiresAtUtc,
            Claims = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [GymOSClaims.Subject] = request.UserId.ToString(),
                [GymOSClaims.Email] = request.Email,
                [GymOSClaims.SecurityStamp] = request.SecurityStamp.ToString(),
                [GymOSClaims.PermissionsHash] = ComputePermissionsHash(request.Permissions),
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                SecurityAlgorithms.HmacSha256),
        };

        string token = new JsonWebTokenHandler().CreateToken(descriptor);

        return new IssuedAccessToken(token, new DateTimeOffset(expiresAtUtc, TimeSpan.Zero));
    }

    /// <summary>
    /// Order-independent fingerprint of a permission set: sorted, joined, SHA-256,
    /// truncated to 128 bits. Sorting matters — the same permissions returned in a
    /// different row order must not look like a permission change to the client.
    /// </summary>
    internal static string ComputePermissionsHash(IReadOnlyCollection<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        string canonical = string.Join(
            '\n',
            permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private static DateTime Floor(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        return new DateTime(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
    }
}
