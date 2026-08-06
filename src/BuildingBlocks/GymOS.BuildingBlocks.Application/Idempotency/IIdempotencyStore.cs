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
/// Result of attempting to claim an idempotency key.
/// </summary>
public sealed record IdempotencyClaimResult(bool Claimed, IdempotencyRecordSnapshot? Existing);

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
    /// <summary>Atomic INSERT … ON CONFLICT DO NOTHING; commits immediately (never inside a transaction).</summary>
    Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Re-leases an expired InProgress claim with the same hash. False if the row moved on (race lost).</summary>
    Task<bool> TryTakeOverAsync(string operationKey, string idempotencyKey, string requestHash, DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken);

    /// <summary>Opens the explicit transaction the handler's SaveChanges will join (AD-39 atomicity).</summary>
    Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Flips the record to Completed/FailedDeterministic; runs inside the ambient transaction.</summary>
    Task MarkFinishedAsync(string operationKey, string idempotencyKey, IdempotencyRecordStatus status, int responseStatus, string? responseBody, CancellationToken cancellationToken);

    /// <summary>Deletes the claim after a transient failure so a retry re-executes; autocommits.</summary>
    Task ReleaseClaimAsync(string operationKey, string idempotencyKey, CancellationToken cancellationToken);
}
