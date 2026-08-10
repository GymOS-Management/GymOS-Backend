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

    /// <summary>
    /// Loads the subscription holding its row lock until the ambient transaction ends
    /// (<c>SELECT … FOR UPDATE</c>). The renewal sweep's read: a console edit racing the sweep
    /// then waits its turn instead of losing at save time — which matters because the sweep has
    /// already drawn a non-refundable invoice number by then, and every lost race burned one.
    /// Callers must hold an <see cref="GymOS.BuildingBlocks.Application.Abstractions.IUnitOfWork"/>
    /// transaction, or the lock releases at the end of the statement.
    /// </summary>
    Task<GymSubscription?> FindByIdForUpdateAsync(Guid subscriptionId, CancellationToken cancellationToken);

    void Add(GymSubscription subscription);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(GymSubscription subscription, uint expectedVersion);
}
