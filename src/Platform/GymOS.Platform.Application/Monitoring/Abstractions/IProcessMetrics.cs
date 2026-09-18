namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>
/// The API process's own last reading. Its own seam for the same reason as <c>IDashboardHealthCheck</c>:
/// the sampler is a host-side <c>BackgroundService</c> over <c>System.Diagnostics.Process</c>, registered
/// by hand in <c>Program.cs</c>. Null until the first sample has been taken.
/// </summary>
public interface IProcessMetrics
{
    ProcessMetrics? Current { get; }
}
