using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace GymOS.Platform.Infrastructure.Idempotency;

/// <summary>
/// AD-39 claims over AppDbContext. The claim INSERT autocommits (no ambient transaction) so
/// every API instance sees it immediately; MarkFinished runs inside the behavior's explicit
/// transaction so record and effect commit atomically. Ownership is fenced by claim_token:
/// MarkFinished and ReleaseClaim only touch rows still carrying the caller's token, so a stale
/// holder that lost its lease to a takeover can neither overwrite nor delete the live claim.
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
    /// The same loop absorbs retention expiry: a conflicting row past <c>expires_at</c> is
    /// AD-39's "expired key behaves as a brand-new request", so it is deleted (dead regardless
    /// of owner — no token check needed) and the INSERT retried.
    /// </summary>
    public async Task<IdempotencyClaimResult> TryClaimAsync(
        IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        for (int attempt = 1; attempt <= MaxClaimAttempts; attempt++)
        {
            var claimToken = Guid.NewGuid();
            // `INSERT … SELECT … WHERE NOT EXISTS` is SQL Server's `ON CONFLICT DO NOTHING`: it
            // reports 1 row affected when the slot was free and 0 when someone already holds it,
            // which is the distinction this loop turns on, and it never throws for the ordinary
            // case of losing the race.
            //
            // The UPDLOCK/HOLDLOCK pair on the probe is what makes it atomic rather than merely
            // check-then-act. Without them two callers can both find no row and both insert, and
            // the unique index turns the loser into a duplicate-key exception — control flow this
            // method is specifically written to avoid, since an exception here would also mark an
            // enclosing transaction doomed. HOLDLOCK takes a key-range lock on the
            // (operation_key, idempotency_key) slot, so the second caller waits for the first to
            // commit and then correctly sees the row.
            int inserted = await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO platform.idempotency_records
                     (id, tenant_id, operation_key, idempotency_key, request_hash, claim_token,
                      status, response_status, response_body, lease_expires_at, created_at, expires_at)
                 SELECT {Guid.NewGuid()}, NULL, {request.OperationKey}, {request.IdempotencyKey},
                        {request.RequestHash}, {claimToken},
                        {(int)IdempotencyRecordStatus.InProgress}, 0, NULL,
                        {request.LeaseExpiresAt}, {request.CreatedAt}, {request.ExpiresAt}
                 WHERE NOT EXISTS (
                     SELECT 1 FROM platform.idempotency_records WITH (UPDLOCK, HOLDLOCK)
                      WHERE operation_key = {request.OperationKey}
                        AND idempotency_key = {request.IdempotencyKey})
                 """,
                cancellationToken);

            if (inserted == 1)
            {
                return new IdempotencyClaimResult(Claimed: true, Existing: null, ClaimToken: claimToken);
            }

            // An expired record behaves as a brand-new request (AD-39 retention): if the row
            // that blocked the INSERT is past its retention expiry, it is dead regardless of
            // who owns it (no token check), so delete it and retry the claim. SYSDATETIMEOFFSET() is
            // the database clock — the same clock the purge maintainer compares against.
            int expiredDeleted = await _dbContext.Database.ExecuteSqlAsync(
                $"""
                 DELETE FROM platform.idempotency_records
                  WHERE operation_key = {request.OperationKey}
                    AND idempotency_key = {request.IdempotencyKey}
                    AND expires_at <= SYSDATETIMEOFFSET()
                 """,
                cancellationToken);

            if (expiredDeleted == 1)
            {
                continue;
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
                        existing.LeaseExpiresAt),
                    ClaimToken: null);
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

    public async Task<Guid?> TryTakeOverAsync(
        string operationKey, string idempotencyKey, string requestHash,
        DateTimeOffset newLeaseExpiresAt, CancellationToken cancellationToken)
    {
        var newToken = Guid.NewGuid();
        int updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET lease_expires_at = {newLeaseExpiresAt},
                    claim_token = {newToken}
              WHERE operation_key = {operationKey}
                AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
                AND lease_expires_at < {newLeaseExpiresAt}
                AND request_hash = {requestHash}
                AND lease_expires_at < SYSDATETIMEOFFSET()
             """,
            cancellationToken);

        return updated == 1 ? newToken : null;
    }

    public async Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfIdempotencyTransaction(await _dbContext.Database.BeginTransactionAsync(cancellationToken));

    public async Task<bool> MarkFinishedAsync(
        string operationKey, string idempotencyKey, Guid claimToken, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken cancellationToken)
    {
        int updated = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             UPDATE platform.idempotency_records
                SET status = {(int)status},
                    response_status = {responseStatus},
                    response_body = {responseBody}
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
                AND claim_token = {claimToken}
             """,
            cancellationToken);

        return updated == 1;
    }

    public async Task<bool> ReleaseClaimAsync(
        string operationKey, string idempotencyKey, Guid claimToken, CancellationToken cancellationToken)
    {
        int deleted = await _dbContext.Database.ExecuteSqlAsync(
            $"""
             DELETE FROM platform.idempotency_records
              WHERE operation_key = {operationKey} AND idempotency_key = {idempotencyKey}
                AND status = {(int)IdempotencyRecordStatus.InProgress}
                AND claim_token = {claimToken}
             """,
            cancellationToken);

        return deleted == 1;
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
