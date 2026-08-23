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

    /// <summary>
    /// The gym's live subscription, row-locked for the caller's transaction.
    /// </summary>
    /// <remarks>
    /// For a caller that must decide something from the live plan and then write based on that
    /// decision. Without the lock a concurrent upgrade can commit between the read and the write,
    /// making the decision stale rather than merely late — and where the write is a delete of rows the
    /// upgrade never touched, nothing else detects it: their concurrency tokens are undisturbed, so no
    /// conflict fires and the wrong outcome commits silently.
    ///
    /// <para><b>The caller must hold an <see cref="IUnitOfWork"/> transaction</b>, or the lock is
    /// released at the end of its own statement and buys nothing.</para>
    /// </remarks>
    Task<GymSubscription?> FindLiveByGymForUpdateAsync(Guid gymId, CancellationToken cancellationToken);

    Task<GymSubscription?> FindByIdAsync(Guid subscriptionId, CancellationToken cancellationToken);

    /// <summary>
    /// Loads the subscription holding its row lock until the ambient transaction ends
    /// (<c>SELECT … WITH (UPDLOCK, ROWLOCK)</c>). The renewal sweep's read: a console edit racing the sweep
    /// then waits its turn instead of losing at save time — which matters because the sweep has
    /// already drawn a non-refundable invoice number by then, and every lost race burned one.
    /// Callers must hold an <see cref="GymOS.BuildingBlocks.Application.Abstractions.IUnitOfWork"/>
    /// transaction, or the lock releases at the end of the statement.
    /// </summary>
    Task<GymSubscription?> FindByIdForUpdateAsync(Guid subscriptionId, CancellationToken cancellationToken);

    void Add(GymSubscription subscription);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(GymSubscription subscription, ulong expectedVersion);
}
