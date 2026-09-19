namespace GymOS.Platform.Application.Settings;

/// <summary>Bound from the <c>maintenance.*</c> settings' <c>ConfigurationPath</c> overlay (M7 spec
/// §3) — the maintenance middleware's own view of whether the platform is closed and what callers
/// are told while it is.</summary>
public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    public bool Enabled { get; set; }

    public string Message { get; set; } = "GymOS is undergoing maintenance. Please try again shortly.";
}
