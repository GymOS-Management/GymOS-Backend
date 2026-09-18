using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>
/// Bound from the <c>Monitoring</c> section. The retention window becomes a <c>platform_settings</c>
/// row when M7 lands; this option is its fallback and shares its name so the key can be the same.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Job runs older than this are swept nightly, except the newest run of each job.</summary>
    [Range(1, 3650)]
    public int JobRunRetentionDays { get; set; } = 30;

    /// <summary>
    /// Added to a job's period before a late run reads as stale, so a merely slow run does not flicker red.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan StaleGrace { get; set; } = TimeSpan.FromMinutes(5);
}
