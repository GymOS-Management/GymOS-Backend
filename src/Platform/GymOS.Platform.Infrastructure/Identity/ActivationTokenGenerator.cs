using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GymOS.Platform.Application.Identity.Abstractions;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Mints activation tokens (ADR-53): 32 bytes from the OS CSPRNG, base64url-encoded so the value
/// survives a URL unescaped, stored as SHA-256.
///
/// <para>Identical arithmetic to <see cref="RefreshTokenGenerator"/> and deliberately not shared with
/// it — see <see cref="IActivationTokenGenerator"/> for why two names beat one implementation here.
/// 256 bits is far past guessable, which is what lets the token be a bare random string rather than a
/// signed structure: there is nothing in it to forge.</para>
/// </summary>
internal sealed class ActivationTokenGenerator : IActivationTokenGenerator
{
    private const int TokenSizeBytes = 32;

    public string Generate() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenSizeBytes));

    public string ComputeHash(string rawToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(rawToken);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }
}
