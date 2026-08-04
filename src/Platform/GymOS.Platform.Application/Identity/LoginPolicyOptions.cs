using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Application.Identity;

/// <summary>
/// Authentication policy the use cases read (configuration section
/// <c>Security:LoginPolicy</c>). Nothing here is a constant in code — lockout
/// thresholds and token lifetimes are operational knobs (ARCHITECTURE.md §2.7).
/// </summary>
public sealed class LoginPolicyOptions
{
    public const string SectionName = "Security:LoginPolicy";

    /// <summary>Consecutive failed attempts that trigger a lockout.</summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long a locked-out account stays locked.</summary>
    [Range(typeof(TimeSpan), "00:00:30", "24:00:00")]
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Refresh-token lifetime — the effective session length (AD-4: 7 days).</summary>
    [Range(typeof(TimeSpan), "00:05:00", "90.00:00:00")]
    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Minimum password length enforced on every password write.</summary>
    [Range(8, 256)]
    public int MinimumPasswordLength { get; set; } = 12;
}
