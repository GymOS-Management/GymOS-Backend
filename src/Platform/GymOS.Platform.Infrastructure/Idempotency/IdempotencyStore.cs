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

    public async Task<IdempotencyClaimResult> TryClaimAsync(
        IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

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

        IdempotencyRecord existing = await _dbContext.Set<IdempotencyRecord>()
            .AsNoTracking()
            .SingleAsync(
                record => record.OperationKey == request.OperationKey
                    && record.IdempotencyKey == request.IdempotencyKey,
                cancellationToken);

        return new IdempotencyClaimResult(
            Claimed: false,
            Existing: new IdempotencyRecordSnapshot(
                (IdempotencyRecordStatus)existing.Status,
                existing.RequestHash,
                existing.ResponseStatus,
                existing.ResponseBody,
                existing.LeaseExpiresAt));
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
