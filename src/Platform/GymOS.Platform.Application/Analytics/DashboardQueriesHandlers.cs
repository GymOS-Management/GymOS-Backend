using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Analytics;

/// <summary>The dashboard's headline numbers, read live (§3.2). See <see cref="IDashboardQueries"/>.</summary>
[RequiresPermission(PlatformPermissions.AnalyticsRead)]
public sealed record GetDashboardSummaryQuery : IQuery<Result<DashboardSummary>>;

/// <summary>The platform's health checks, projected for a console panel. See <see cref="IDashboardHealthCheck"/>.</summary>
[RequiresPermission(PlatformPermissions.AnalyticsRead)]
public sealed record GetDashboardHealthQuery : IQuery<Result<DashboardHealth>>;

/// <summary>
/// The audit log in a trimmed shape, for the dashboard's activity feed. Gated on
/// <see cref="PlatformPermissions.AuditRead"/> rather than <see cref="PlatformPermissions.AnalyticsRead"/>
/// — this is the audit log wearing a different hat, and audit access must not depend on analytics
/// access, or the reverse.
/// </summary>
[RequiresPermission(PlatformPermissions.AuditRead)]
public sealed record GetRecentActivityQuery(int Limit) : IQuery<Result<IReadOnlyList<ActivityEntry>>>;

internal sealed class GetDashboardSummaryQueryHandler
    : IQueryHandler<GetDashboardSummaryQuery, Result<DashboardSummary>>
{
    private readonly IDashboardQueries _dashboardQueries;

    public GetDashboardSummaryQueryHandler(IDashboardQueries dashboardQueries) =>
        _dashboardQueries = dashboardQueries;

    public async Task<Result<DashboardSummary>> HandleAsync(
        GetDashboardSummaryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Result.Success(await _dashboardQueries.GetSummaryAsync(cancellationToken));
    }
}

internal sealed class GetDashboardHealthQueryHandler
    : IQueryHandler<GetDashboardHealthQuery, Result<DashboardHealth>>
{
    private readonly IDashboardHealthCheck _healthCheck;

    public GetDashboardHealthQueryHandler(IDashboardHealthCheck healthCheck) => _healthCheck = healthCheck;

    public async Task<Result<DashboardHealth>> HandleAsync(
        GetDashboardHealthQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Result.Success(await _healthCheck.CheckAsync(cancellationToken));
    }
}

internal sealed class GetRecentActivityQueryHandler
    : IQueryHandler<GetRecentActivityQuery, Result<IReadOnlyList<ActivityEntry>>>
{
    private readonly IDashboardQueries _dashboardQueries;

    public GetRecentActivityQueryHandler(IDashboardQueries dashboardQueries) =>
        _dashboardQueries = dashboardQueries;

    public async Task<Result<IReadOnlyList<ActivityEntry>>> HandleAsync(
        GetRecentActivityQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // An unbounded limit is a table scan a caller can request by accident.
        int limit = Math.Clamp(query.Limit, 1, 100);

        return Result.Success(await _dashboardQueries.GetRecentActivityAsync(limit, cancellationToken));
    }
}
