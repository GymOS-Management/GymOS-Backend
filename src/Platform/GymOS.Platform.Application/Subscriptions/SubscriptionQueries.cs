using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>A gym's current subscription. Read with <c>billing.read</c> — it is commercial information.</summary>
[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record GetGymSubscriptionQuery(Guid GymId) : IQuery<Result<SubscriptionDetail>>;

internal sealed class GetGymSubscriptionQueryHandler
    : IQueryHandler<GetGymSubscriptionQuery, Result<SubscriptionDetail>>
{
    private readonly ISubscriptionQueries _subscriptionQueries;

    public GetGymSubscriptionQueryHandler(ISubscriptionQueries subscriptionQueries) =>
        _subscriptionQueries = subscriptionQueries;

    public async Task<Result<SubscriptionDetail>> HandleAsync(
        GetGymSubscriptionQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        SubscriptionDetail? detail = await _subscriptionQueries.GetLiveForGymAsync(
            query.GymId, cancellationToken);

        return detail is null
            ? Result.Failure<SubscriptionDetail>(SubscriptionErrors.NotFound(query.GymId))
            : Result.Success(detail);
    }
}

/// <summary>
/// Every subscription the gym has had, newest first — what a billing dispute is settled from.
/// </summary>
[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record ListGymSubscriptionHistoryQuery(Guid GymId)
    : IQuery<Result<IReadOnlyList<SubscriptionDetail>>>;

internal sealed class ListGymSubscriptionHistoryQueryHandler
    : IQueryHandler<ListGymSubscriptionHistoryQuery, Result<IReadOnlyList<SubscriptionDetail>>>
{
    private readonly ISubscriptionQueries _subscriptionQueries;

    public ListGymSubscriptionHistoryQueryHandler(ISubscriptionQueries subscriptionQueries) =>
        _subscriptionQueries = subscriptionQueries;

    public async Task<Result<IReadOnlyList<SubscriptionDetail>>> HandleAsync(
        ListGymSubscriptionHistoryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<SubscriptionDetail> history = await _subscriptionQueries.ListHistoryForGymAsync(
            query.GymId, cancellationToken);

        return Result.Success(history);
    }
}
