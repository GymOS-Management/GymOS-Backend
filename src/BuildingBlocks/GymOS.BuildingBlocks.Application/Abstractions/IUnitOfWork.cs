namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Commits the work of one use case. Handlers mutate aggregates through repositories
/// and call this exactly once at the end: the single save is what makes state, the
/// audit rows, and the outbox rows commit in one transaction (AD-13/AD-39). No
/// EF Core type crosses into the Application layer — the abstraction is this method
/// and nothing more (AD-3).
/// </summary>
public interface IUnitOfWork
{
    /// <exception cref="ConcurrencyConflictException">
    /// A row carrying a concurrency token changed after it was read. Implementations translate
    /// the provider's own concurrency failure into this, so no handler and no endpoint has to
    /// know what the persistence layer throws.
    /// </exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Opens an explicit transaction so reads taken with locks (a repository's for-update
    /// lookup) hold them until <see cref="IUnitOfWorkTransaction.CommitAsync"/>. Most request
    /// handlers do not need this — one save is already one transaction — but a handler that reads
    /// a second aggregate with a for-update lock before deciding what to write needs the lock held
    /// across both the read and the eventual save, which requires wrapping them in one of these.
    /// Disposing without committing rolls back.
    /// </summary>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Detaches every tracked entity, discarding whatever a failed <see cref="SaveChangesAsync"/>
    /// left mid-flight.
    ///
    /// <para>Exists for a handler that catches a transient persistence failure — a unique-index
    /// race it can resolve by retrying itself once — and re-runs its own logic from scratch.
    /// Without this, the retry's fresh reads merge into entities the failed attempt already added
    /// or modified (an insert the database never committed is still tracked as <c>Added</c>), so
    /// the second attempt would try to write the first attempt's poisoned state alongside its own
    /// and fail the same way. Calling this first makes the retry start from what is actually
    /// committed, exactly as if it were a fresh request.</para>
    ///
    /// <para><b>Banned outside <c>SetGymFeaturesCommandHandler</c>'s retry</b> (BannedSymbols.txt, RS0030):
    /// it clears every tracked entity in the scope, not only the caller's, so any other use would
    /// silently drop rows another component staged into the same context.</para>
    /// </summary>
    void DiscardTrackedChanges();
}

/// <summary>An explicit transaction from <see cref="IUnitOfWork.BeginTransactionAsync"/>.</summary>
public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken);
}
