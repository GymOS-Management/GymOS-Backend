using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Access-token settings (AD-4). <see cref="SigningKey"/> is a secret: user-secrets
/// locally, Key Vault in production (AD-11) — never a value committed to a config
/// file. Startup validation fails fast when it is missing or too short, because a
/// weakly signed token is worse than no token at all: it looks like security.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Security:Jwt";

    /// <summary>Minimum key length in bytes. HMAC-SHA256 keys shorter than the hash output add no security.</summary>
    public const int MinimumSigningKeyBytes = 32;

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Symmetric signing key, at least <see cref="MinimumSigningKeyBytes"/> bytes of UTF-8.</summary>
    [Required]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Access-token lifetime — 15 minutes per AD-4.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "12:00:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Tolerance for validator/issuer clock differences. Zero here and deliberately so:
    /// issuer and validator are the same process for now, and the framework's five-minute
    /// default silently extends every token's life by a third of its lifetime.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.Zero;
}
