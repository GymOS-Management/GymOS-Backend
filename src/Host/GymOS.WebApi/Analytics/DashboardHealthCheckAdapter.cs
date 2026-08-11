using GymOS.Platform.Application.Analytics.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GymOS.WebApi.Analytics;

/// <summary>
/// Adapts ASP.NET Core's <see cref="HealthCheckService"/> to <see cref="IDashboardHealthCheck"/>.
///
/// <para>Lives in the host rather than an Infrastructure project: <see cref="HealthCheckService"/>
/// and <see cref="HealthReport"/> ship in the ASP.NET Core shared framework, which only a
/// <c>Microsoft.NET.Sdk.Web</c> project references — Infrastructure is plain <c>Microsoft.NET.Sdk</c>,
/// same as Application. This is the one place that boundary is crossed, registered by hand in
/// <c>Program.cs</c> rather than through <c>AddPlatformAnalytics</c> for the same reason.</para>
/// </summary>
internal sealed class DashboardHealthCheckAdapter : IDashboardHealthCheck
{
    private readonly HealthCheckService _healthCheckService;

    public DashboardHealthCheckAdapter(HealthCheckService healthCheckService) =>
        _healthCheckService = healthCheckService;

    public async Task<DashboardHealth> CheckAsync(CancellationToken cancellationToken)
    {
        HealthReport report = await _healthCheckService.CheckHealthAsync(cancellationToken);

        // No Exception, no Description projected here: this feeds a console panel, not a debugger,
        // and an exception message can carry a connection string.
        return new DashboardHealth(
            report.Status.ToString(),
            report.Entries
                .Select(entry => new HealthCheckStatus(entry.Key, entry.Value.Status.ToString()))
                .ToList());
    }
}
