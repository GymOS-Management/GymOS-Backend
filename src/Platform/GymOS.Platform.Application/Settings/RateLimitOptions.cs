using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Application.Settings;

/// <summary>Bound from the <c>security.*_requests_per_minute</c>/<c>sign_in_*</c> settings'
/// <c>ConfigurationPath</c> overlay (M7 spec §3) — the rate limiter's own view of the effective
/// limits, refreshed as the settings cache reloads.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Security:RateLimiting";

    [Range(1, 1000)]
    public int SignInAttemptsPerWindow { get; set; } = 10;

    [Range(1, 3600)]
    public int SignInWindowSeconds { get; set; } = 60;

    [Range(1, 100000)]
    public int ApiRequestsPerMinute { get; set; } = 600;

    [Range(1, 100000)]
    public int AnonymousRequestsPerMinute { get; set; } = 60;
}
