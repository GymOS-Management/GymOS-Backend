using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Argon2id cost parameters (AD-4: "parameters from configuration"). Defaults follow
/// the OWASP Password Storage recommendation for Argon2id — 19 MiB of memory, two
/// iterations, one degree of parallelism — which is the setting deliberately chosen to
/// be tunable rather than compiled in: hardware moves, and raising cost must be a
/// configuration change, not a release.
///
/// Raising these values never invalidates existing hashes: every stored hash carries
/// the parameters it was produced with (see <see cref="Argon2PasswordHasher"/>).
/// </summary>
public sealed class PasswordHashingOptions
{
    public const string SectionName = "Security:PasswordHashing";

    /// <summary>Memory cost in kibibytes. Higher is the strongest defense against GPU cracking.</summary>
    [Range(8 * 1024, 1024 * 1024)]
    public int MemorySizeKib { get; set; } = 19 * 1024;

    /// <summary>Time cost: passes over memory.</summary>
    [Range(1, 20)]
    public int Iterations { get; set; } = 2;

    /// <summary>Lanes used in parallel. Keep at or below the container's CPU allowance.</summary>
    [Range(1, 16)]
    public int DegreeOfParallelism { get; set; } = 1;

    /// <summary>Salt length in bytes; 16 is the Argon2 recommendation.</summary>
    [Range(16, 64)]
    public int SaltSizeBytes { get; set; } = 16;

    /// <summary>Derived-key length in bytes; 32 gives a 256-bit tag.</summary>
    [Range(16, 64)]
    public int HashSizeBytes { get; set; } = 32;
}
