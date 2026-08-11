using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics.Rules;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Analytics;

/// <summary>Daily MRR, recorded days only. See <see cref="IAnalyticsQueries"/>.</summary>
[RequiresPermission(PlatformPermissions.AnalyticsRead)]
public sealed record GetRevenueQuery(DateOnly From, DateOnly To) : IQuery<Result<IReadOnlyList<RevenuePoint>>>;

/// <summary>Daily gym counts, recorded days only. See <see cref="IAnalyticsQueries"/>.</summary>
[RequiresPermission(PlatformPermissions.AnalyticsRead)]
public sealed record GetGymsGrowthQuery(DateOnly From, DateOnly To) : IQuery<Result<IReadOnlyList<GrowthPoint>>>;

/// <summary>Daily churn, canceled and expired kept apart. See <see cref="IAnalyticsQueries"/>.</summary>
[RequiresPermission(PlatformPermissions.AnalyticsRead)]
public sealed record GetChurnQuery(DateOnly From, DateOnly To) : IQuery<Result<IReadOnlyList<ChurnPoint>>>;

internal sealed class GetRevenueQueryHandler : IQueryHandler<GetRevenueQuery, Result<IReadOnlyList<RevenuePoint>>>
{
    private readonly IAnalyticsQueries _analyticsQueries;

    public GetRevenueQueryHandler(IAnalyticsQueries analyticsQueries) => _analyticsQueries = analyticsQueries;

    public async Task<Result<IReadOnlyList<RevenuePoint>>> HandleAsync(
        GetRevenueQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Result validation = AnalyticsRange.Validate(query.From, query.To);
        if (validation.IsFailure)
        {
            return Result.Failure<IReadOnlyList<RevenuePoint>>(validation.Error!);
        }

        return Result.Success(await _analyticsQueries.GetRevenueAsync(query.From, query.To, cancellationToken));
    }
}

internal sealed class GetGymsGrowthQueryHandler
    : IQueryHandler<GetGymsGrowthQuery, Result<IReadOnlyList<GrowthPoint>>>
{
    private readonly IAnalyticsQueries _analyticsQueries;

    public GetGymsGrowthQueryHandler(IAnalyticsQueries analyticsQueries) => _analyticsQueries = analyticsQueries;

    public async Task<Result<IReadOnlyList<GrowthPoint>>> HandleAsync(
        GetGymsGrowthQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Result validation = AnalyticsRange.Validate(query.From, query.To);
        if (validation.IsFailure)
        {
            return Result.Failure<IReadOnlyList<GrowthPoint>>(validation.Error!);
        }

        return Result.Success(await _analyticsQueries.GetGymsGrowthAsync(query.From, query.To, cancellationToken));
    }
}

internal sealed class GetChurnQueryHandler : IQueryHandler<GetChurnQuery, Result<IReadOnlyList<ChurnPoint>>>
{
    private readonly IAnalyticsQueries _analyticsQueries;

    public GetChurnQueryHandler(IAnalyticsQueries analyticsQueries) => _analyticsQueries = analyticsQueries;

    public async Task<Result<IReadOnlyList<ChurnPoint>>> HandleAsync(
        GetChurnQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        Result validation = AnalyticsRange.Validate(query.From, query.To);
        if (validation.IsFailure)
        {
            return Result.Failure<IReadOnlyList<ChurnPoint>>(validation.Error!);
        }

        return Result.Success(await _analyticsQueries.GetChurnAsync(query.From, query.To, cancellationToken));
    }
}
