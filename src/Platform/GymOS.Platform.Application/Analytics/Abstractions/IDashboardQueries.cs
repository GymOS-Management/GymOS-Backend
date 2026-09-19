namespace GymOS.Platform.Application.Analytics.Abstractions;

/// <summary>An amount with its currency, because a bare decimal has no meaning across a wire (R4).</summary>
public sealed record MoneyDto(decimal Amount, string Currency);

/// <summary>
/// What is true right now (§3.2). Read live from the operational tables: AD-8's read model exists so
/// dashboards never scan <em>tenant</em> tables, and these counts are O(gyms). A summary served from
/// yesterday's row would not be a trade-off, it would be wrong.
/// </summary>
public sealed record DashboardSummary(
    int TotalGyms,
    int ActiveGyms,
    int SuspendedGyms,
    int ProvisioningGyms,
    int LiveSubscriptions,
    IReadOnlyList<MoneyDto> Mrr);

public sealed record HealthCheckStatus(string Name, string Status);

public sealed record DashboardHealth(string Status, IReadOnlyList<HealthCheckStatus> Checks);

public sealed record ActivityEntry(
    DateTimeOffset OccurredAtUtc, string Action, string? EntityType, string? UserEmail);

/// <summary>
/// Live reads for the dashboard (§3.2). Everything here queries the operational tables at request
/// time — nothing reads AD-8's daily read model, which exists for the analytics *series*, not this.
/// </summary>
public interface IDashboardQueries
{
    Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEntry>> GetRecentActivityAsync(int limit, CancellationToken cancellationToken);
}

/// <summary>
/// A live probe of the platform's health checks (§3.2) — the same report <c>/health/ready</c> runs.
///
/// <para>Its own seam, separate from <see cref="IDashboardQueries"/>, because the thing it wraps
/// (<c>HealthCheckService</c>/<c>HealthReport</c>) ships in the ASP.NET Core shared framework rather
/// than a package this layer can reference: Application and Infrastructure are plain
/// <c>Microsoft.NET.Sdk</c> projects, and only the host takes on <c>Microsoft.NET.Sdk.Web</c>. The
/// implementation therefore lives in <c>GymOS.WebApi</c>, wired in <c>Program.cs</c> — the one place
/// that boundary is allowed to be crossed — while the query and its handler stay here, so
/// <c>/dashboard/health</c> goes through the dispatcher exactly like every other endpoint and gets
/// AD-5's second permission check for free.</para>
/// </summary>
public interface IDashboardHealthCheck
{
    Task<DashboardHealth> CheckAsync(CancellationToken cancellationToken);
}
