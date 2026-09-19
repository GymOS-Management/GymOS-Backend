using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>How long <see cref="SettingsProvider"/> serves a cached snapshot before reloading from
/// the database (M7 spec §3) — the window a change made on one replica takes to reach the others.</summary>
public sealed class SettingsProviderOptions
{
    public const string SectionName = "Settings";

    [Range(1, 3600)]
    public int CacheTtlSeconds { get; set; } = 30;
}
