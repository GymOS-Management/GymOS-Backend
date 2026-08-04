using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using GymOS.Platform.Application.Identity.Abstractions;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Argon2id password hashing (AD-4), storing hashes in the PHC string format —
/// <c>$argon2id$v=19$m=19456,t=2,p=1$&lt;salt&gt;$&lt;hash&gt;</c>.
///
/// The format is the point. Verification reads its cost parameters *out of the stored
/// hash* rather than out of configuration, so raising cost affects new passwords only
/// and every existing user can still sign in. A scheme that verified with the current
/// configuration would lock out the entire user base the moment an operator tuned a
/// parameter — the classic way password upgrades go wrong.
/// </summary>
internal sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const string AlgorithmId = "argon2id";
    private const int Argon2Version = 19;

    /// <summary>
    /// Konscious refuses a zero-length password outright, which would turn an empty
    /// password field into a 500 — including on the sign-in path, where an empty password
    /// is hashed deliberately to equalize response time for unknown accounts. An empty
    /// input is therefore derived from this fixed one-byte stand-in instead.
    ///
    /// It is not a security-relevant substitution: no password this short can be *stored*
    /// (<c>PasswordPolicy</c> enforces a minimum length on every write path), so no
    /// account can ever hold the resulting hash to be matched against.
    /// </summary>
    private static readonly byte[] EmptyPasswordSurrogate = [0x00];

    private readonly IOptions<PasswordHashingOptions> _options;

    public Argon2PasswordHasher(IOptions<PasswordHashingOptions> options) => _options = options;

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        PasswordHashingOptions options = _options.Value;
        byte[] salt = RandomNumberGenerator.GetBytes(options.SaltSizeBytes);

        byte[] hash = Derive(
            password,
            salt,
            options.MemorySizeKib,
            options.Iterations,
            options.DegreeOfParallelism,
            options.HashSizeBytes);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"${AlgorithmId}$v={Argon2Version}$m={options.MemorySizeKib},t={options.Iterations},p={options.DegreeOfParallelism}${ToUnpaddedBase64(salt)}${ToUnpaddedBase64(hash)}");
    }

    public bool Verify(string password, string passwordHash)
    {
        if (password is null || !TryParse(passwordHash, out Argon2Hash parsed))
        {
            return false;
        }

        byte[] candidate = Derive(
            password,
            parsed.Salt,
            parsed.MemorySizeKib,
            parsed.Iterations,
            parsed.DegreeOfParallelism,
            parsed.Hash.Length);

        return CryptographicOperations.FixedTimeEquals(candidate, parsed.Hash);
    }

    private static byte[] Derive(
        string password,
        byte[] salt,
        int memorySizeKib,
        int iterations,
        int degreeOfParallelism,
        int hashSizeBytes)
    {
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);

        using var argon2 = new Argon2id(passwordBytes.Length == 0 ? EmptyPasswordSurrogate : passwordBytes)
        {
            Salt = salt,
            MemorySize = memorySizeKib,
            Iterations = iterations,
            DegreeOfParallelism = degreeOfParallelism,
        };

        return argon2.GetBytes(hashSizeBytes);
    }

    private sealed record Argon2Hash(
        int MemorySizeKib,
        int Iterations,
        int DegreeOfParallelism,
        byte[] Salt,
        byte[] Hash);

    /// <summary>
    /// Parses a PHC-format Argon2id hash. Every malformed input returns
    /// <see langword="false"/> rather than throwing: a corrupt or foreign hash must be
    /// indistinguishable from a wrong password at the call site, and a stored value is
    /// untrusted input like any other.
    /// </summary>
    private static bool TryParse(string? value, out Argon2Hash parsed)
    {
        parsed = null!;

        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        // Leading empty segment from the initial '$': ["", "argon2id", "v=19", "m=..,t=..,p=..", salt, hash]
        string[] segments = value.Split('$');
        if (segments.Length != 6 ||
            segments[0].Length != 0 ||
            !string.Equals(segments[1], AlgorithmId, StringComparison.Ordinal) ||
            !string.Equals(segments[2], $"v={Argon2Version}", StringComparison.Ordinal))
        {
            return false;
        }

        string[] costs = segments[3].Split(',');
        if (costs.Length != 3 ||
            !TryReadCost(costs[0], "m", out int memorySizeKib) ||
            !TryReadCost(costs[1], "t", out int iterations) ||
            !TryReadCost(costs[2], "p", out int degreeOfParallelism))
        {
            return false;
        }

        if (!TryFromUnpaddedBase64(segments[4], out byte[] salt) ||
            !TryFromUnpaddedBase64(segments[5], out byte[] hash) ||
            salt.Length == 0 ||
            hash.Length == 0)
        {
            return false;
        }

        parsed = new Argon2Hash(memorySizeKib, iterations, degreeOfParallelism, salt, hash);
        return true;
    }

    private static bool TryReadCost(string segment, string name, out int cost)
    {
        cost = 0;
        string prefix = name + "=";

        return segment.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(segment.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out cost) &&
            cost > 0;
    }

    private static string ToUnpaddedBase64(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=');

    private static bool TryFromUnpaddedBase64(string value, out byte[] bytes)
    {
        bytes = [];

        int padding = (4 - (value.Length % 4)) % 4;
        if (value.Length == 0 || padding == 3)
        {
            return false; // no valid base64 length leaves three characters over
        }

        try
        {
            bytes = Convert.FromBase64String(value + new string('=', padding));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
