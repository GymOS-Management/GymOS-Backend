namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Mints and hashes the opaque refresh tokens (AD-4). The raw token exists only in
/// the response cookie; the database stores its SHA-256 hash, so a database read
/// never yields a usable credential. Hashing is unsalted and deterministic by
/// design — lookup is by hash, and the input is already 256 bits of entropy, which
/// leaves nothing for a salt to protect against.
/// </summary>
public interface IRefreshTokenGenerator
{
    /// <summary>A cryptographically random, URL-safe opaque token.</summary>
    string GenerateRawToken();

    /// <summary>The stored form: SHA-256 of the raw token, lowercase hex.</summary>
    string ComputeHash(string rawToken);
}
