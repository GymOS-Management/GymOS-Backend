namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Status of an idempotency record in the store.
/// </summary>
public enum IdempotencyRecordStatus { InProgress = 1, Completed = 2, FailedDeterministic = 3 }

/// <summary>
/// Snapshot of a stored idempotency record.
/// </summary>
public sealed record IdempotencyRecordSnapshot(
    IdempotencyRecordStatus Status,
    string RequestHash,
    int ResponseStatus,
    string? ResponseBody,
    DateTimeOffset LeaseExpiresAt);

/// <summary>
/// Request to claim an idempotency key for a new operation execution.
/// </summary>
public sealed record IdempotencyClaimRequest(
    string OperationKey,
    string IdempotencyKey,
    string RequestHash,
    DateTimeOffset LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Result of attempting to claim an idempotency key. <paramref name="ClaimToken"/> is the fencing
/// token identifying this claim's owner: non-null exactly when <paramref name="Claimed"/> is true,
/// and required by <see cref="IIdempotencyStore.MarkFinishedAsync"/> /
/// <see cref="IIdempotencyStore.ReleaseClaimAsync"/> so a stale holder whose lease was taken over
/// can never overwrite or delete the new owner's record.
/// </summary>
public sealed record IdempotencyClaimResult(bool Claimed, IdempotencyRecordSnapshot? Existing, Guid? ClaimToken);

/// <summary>
/// Transaction context for idempotency operations.
/// </summary>
public interface IIdempotencyTransaction : IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rolls back the transaction.
    /// </summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persistence seam for AD-39 claims. Application cannot reference EF Core (layer law),
/// so the implementation lives in Platform.Infrastructure over AppDbContext.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomic INSERT … ON CONFLICT DO NOTHING; commits immediately (never inside a transaction).
    /// A conflicting row past its retention expiry is treated as absent (deleted and re-claimed),
    /// so an expired key behaves as a brand-new request, per AD-39.
    /// </summary>
    Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Re-leases an expired InProgress claim with the same hash, fencing out the stale holder by
    /// replacing the claim token. Returns the new token, or null if the row moved on (race lost).
    /// </summary>
    Task<Guid?> TryTakeOverAsync(string operationKey, string idempotencyKey, string requestHash, DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken);

    /// <summary>Opens the explicit transaction the handler's SaveChanges will join (AD-39 atomicity).</summary>
    Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Flips the record to Completed/FailedDeterministic; runs inside the ambient transaction.
    /// Gated on <paramref name="claimToken"/>: returns false when no row was updated, which means
    /// the lease was lost to a takeover — the caller must roll back its transaction, because the
    /// new holder owns the outcome.
    /// </summary>
    Task<bool> MarkFinishedAsync(string operationKey, string idempotencyKey, Guid claimToken, IdempotencyRecordStatus status, int responseStatus, string? responseBody, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the claim after a transient failure so a retry re-executes; autocommits.
    /// Gated on <paramref name="claimToken"/>: returns false when no row was deleted (the claim
    /// was taken over and now belongs to someone else) — which is fine; never delete unowned rows.
    /// </summary>
    Task<bool> ReleaseClaimAsync(string operationKey, string idempotencyKey, Guid claimToken, CancellationToken cancellationToken);
}
