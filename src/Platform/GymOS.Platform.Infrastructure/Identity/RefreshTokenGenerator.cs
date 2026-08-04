using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GymOS.Platform.Application.Identity.Abstractions;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Mints opaque refresh tokens (AD-4): 32 bytes from the OS CSPRNG, base64url-encoded
/// so the value survives a cookie round-trip unescaped. 256 bits is far past guessable,
/// which is what lets the token be a bare random string rather than a signed structure —
/// there is nothing in it to forge.
/// </summary>
internal sealed class RefreshTokenGenerator : IRefreshTokenGenerator
{
    private const int TokenSizeBytes = 32;

    public string GenerateRawToken() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenSizeBytes));

    public string ComputeHash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }
}
