using System.ComponentModel.DataAnnotations;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>Bound from the "Idempotency" section, validated on start (house options pattern).</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Configuration section name this type binds to.</summary>
    public const string SectionName = "Idempotency";

    /// <summary>How long a claim blocks concurrent duplicates before a crashed attempt can be taken over.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "00:10:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Delete expired records at startup until the M3.4 retention job takes over.</summary>
    public bool PurgeOnStartup { get; set; } = true;
}
