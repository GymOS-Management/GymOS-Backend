using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.Subscriptions.Abstractions;

/// <summary>Write-side access to the <see cref="GymSubscription"/> aggregate (AD-3).</summary>
public interface ISubscriptionRepository
{
    /// <summary>
    /// The gym's live subscription (Trialing, Active, or PastDue), or null when it has none.
    ///
    /// Only one can exist — a partial unique index guarantees it — so this returns a single row rather
    /// than making every caller decide which of several is current.
    /// </summary>
    Task<GymSubscription?> FindLiveByGymAsync(Guid gymId, CancellationToken cancellationToken);

    Task<GymSubscription?> FindByIdAsync(Guid subscriptionId, CancellationToken cancellationToken);

    void Add(GymSubscription subscription);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(GymSubscription subscription, uint expectedVersion);
}
