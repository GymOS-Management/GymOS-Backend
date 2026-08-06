using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Idempotency;

/// <summary>
/// AD-39 claims over AppDbContext. The claim INSERT autocommits (no ambient transaction) so
/// every API instance sees it immediately; MarkFinished runs inside the behavior's explicit
/// transaction so record and effect commit atomically.
/// </summary>
internal sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly AppDbContext _dbContext;

    public IdempotencyStore(AppDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Bounds the retry described on <see cref="TryClaimAsync"/>; pathological
    /// contention (not a normal race) past this many attempts is a bug worth surfacing.</summary>
    private const int MaxClaimAttempts = 3;

    /// <summary>
    /// Loops rather than doing one INSERT-then-lookup because the lookup can race
    /// <see cref="ReleaseClaimAsync"/>: if the row that lost the INSERT is deleted by its
    /// owner (a transient failure releasing its claim) in the window before the lookup runs,
    /// the row is simply gone — not "someone else legitimately holds it". In that case there
    /// is nothing to report as <c>Existing</c>, and the honest outcome is the same as if this
    /// call had run a moment later and won the INSERT outright, so it retries the INSERT
    /// instead of surfacing an error for a state that already resolved itself.
    /// </summary>
    public async Task<IdempotencyClaimResult> TryClaimAsync(
        IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (int attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            int inserted = await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO platform.idempotency_records
                     (id, tenant_id, operation_key, idempotency_key, request_hash, status,
                      response_status, response_body, lease_expires_at, created_at, expires_at)
                 VALUES ({Guid.NewGuid()}, NULL, {request.OperationKey}, {request.IdempotencyKey},
                         {request.RequestHash}, {(int)IdempotencyRecordStatus.InProgress}, 0, NULL,
                         {request.LeaseExpiresAt}, {request.CreatedAt}, {request.ExpiresAt})
                 ON CONFLICT DO NOTHING
                 """,
                cancellationToken);

            if (inserted == 1)
            {
                return new IdempotencyClaimResult(Claimed: true, Existing: null);
            }

            IdempotencyRecord? existing = await _dbContext.Set<IdempotencyRecord>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    record => record.OperationKey == request.OperationKey
                        && record.IdempotencyKey == request.IdempotencyKey,
                    cancellationToken);

            if (existing is not null)
            {
                return new IdempotencyClaimResult(
                    Claimed: false,
                    Existing: new IdempotencyRecordSnapshot(
                        (IdempotencyRecordStatus)existing.Status,
                        existing.RequestHash,
                        existing.ResponseStatus,
                        existing.ResponseBody,
                        existing.LeaseExpiresAt));
            }

            // The row that beat our INSERT is already gone (released between the INSERT and
            // this lookup) — the slot is free again, so loop back and take it.
        }

        throw new InvalidOperationException(
            $"Idempotency claim for {request.OperationKey}/{request.IdempotencyKey} did not " +
            $"settle after {MaxClaimAttempts} attempts — the claim slot kept being released " +
            "out from under this call, which points at pathological contention rather than a " +
            "normal race.");
    }

    public async Task<bool> TryTakeOverAsync(
        string operationKey, string idempotencyKey, string requestHash,
        DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken)
    {
        int updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET lease_expires_at = {newLeaseExpiresAt}
              WHERE operation_key = {operationKey}
                AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
                AND lease_expires_at < {newLeaseExpiresAt}
                AND request_hash = {requestHash}
                AND lease_expires_at < now()
             """,
            cancellationToken);

        return updated == 1;
    }

    public async Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfIdempotencyTransaction(await _dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task MarkFinishedAsync(
        string operationKey, string idempotencyKey, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET status = {(int)status},
                    response_status = {responseStatus},
                    response_body = {responseBody}::jsonb
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
             """,
            cancellationToken);
    }

    public async Task ReleaseClaimAsync(
        string operationKey, string idempotencyKey, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM platform.idempotency_records
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
             """,
            cancellationToken);
    }

    private sealed class EfIdempotencyTransaction : IIdempotencyTransaction
    {
        private readonly IDbContextTransaction _transaction;

        public EfIdempotencyTransaction(IDbContextTransaction transaction) => _transaction = transaction;

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) => _transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
