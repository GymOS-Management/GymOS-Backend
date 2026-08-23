using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>The precondition and read-back every subscription write shares.</summary>
internal static class SubscriptionWrites
{
    /// <summary>
    /// Applies the caller's <c>If-Match</c>. Honoured but not demanded on these action endpoints: the
    /// status transition table already refuses the interleavings that matter (cancelling twice,
    /// converting a subscription that is not trialing).
    /// </summary>
    public static Result ApplyExpectedVersion(
        ISubscriptionRepository subscriptions,
        GymSubscription subscription,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired ? Result.Failure(SubscriptionErrors.VersionRequired) : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out ulong parsed))
        {
            return Result.Failure(SubscriptionErrors.MalformedVersion);
        }

        subscriptions.ExpectVersion(subscription, parsed);
        return Result.Success();
    }

    public static async Task<Result<SubscriptionDetail>> ReadBackAsync(
        ISubscriptionQueries subscriptionQueries,
        Guid gymId,
        CancellationToken cancellationToken)
    {
        SubscriptionDetail? detail = await subscriptionQueries.GetLiveForGymAsync(gymId, cancellationToken);

        return detail is null
            ? Result.Failure<SubscriptionDetail>(SubscriptionErrors.NotFound(gymId))
            : Result.Success(detail);
    }
}
