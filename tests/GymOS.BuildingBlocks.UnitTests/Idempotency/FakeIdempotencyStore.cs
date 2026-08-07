using System.Collections.Concurrent;
using GymOS.BuildingBlocks.Application.Idempotency;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

/// <summary>In-memory IIdempotencyStore mirroring the real store's observable semantics,
/// including the claim-token fence: MarkFinished/ReleaseClaim only touch a row still carrying
/// the caller's token, exactly like the real store's <c>AND claim_token = …</c> SQL.</summary>
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private sealed record Row(string RequestHash, Guid ClaimToken, IdempotencyRecordStatus Status,
        int ResponseStatus, string? ResponseBody, DateTimeOffset LeaseExpiresAt);

    private readonly ConcurrentDictionary<(string Op, string Key), Row> _rows = new();

    public bool CommittedInTransaction { get; private set; }
    public bool LastTransactionRolledBack { get; private set; }
    public bool IsEmpty => _rows.IsEmpty;

    public IdempotencyRecordSnapshot Single(string op, string key)
    {
        Row row = _rows[(op, key)];
        return new IdempotencyRecordSnapshot(row.Status, row.RequestHash, row.ResponseStatus, row.ResponseBody, row.LeaseExpiresAt);
    }

    public void SeedInProgress(string op, string key, string hash, DateTimeOffset leaseExpiresAt) =>
        _rows[(op, key)] = new Row(hash, Guid.NewGuid(), IdempotencyRecordStatus.InProgress, 0, null, leaseExpiresAt);

    /// <summary>Simulates another holder taking the claim over mid-flight: the row gets a NEW
    /// token, so the original claimant's MarkFinished/ReleaseClaim affect zero rows — the fence.</summary>
    public void SimulateTakeoverByAnotherHolder(string op, string key) =>
        _rows[(op, key)] = _rows[(op, key)] with { ClaimToken = Guid.NewGuid() };

    /// <summary>When set, every <see cref="TryTakeOverAsync"/> reports the race as lost.</summary>
    public bool FailTakeovers { get; set; }

    /// <summary>When set, <see cref="ReleaseClaimAsync"/> throws — simulating the store being
    /// unreachable during cleanup, which must never mask the handler's original exception.</summary>
    public bool ThrowOnRelease { get; set; }

    public Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        var token = Guid.NewGuid();
        bool claimed = _rows.TryAdd(
            (request.OperationKey, request.IdempotencyKey),
            new Row(request.RequestHash, token, IdempotencyRecordStatus.InProgress, 0, null, request.LeaseExpiresAt));

        if (claimed)
        {
            return Task.FromResult(new IdempotencyClaimResult(true, null, token));
        }

        Row row = _rows[(request.OperationKey, request.IdempotencyKey)];
        return Task.FromResult(new IdempotencyClaimResult(false,
            new IdempotencyRecordSnapshot(row.Status, row.RequestHash, row.ResponseStatus, row.ResponseBody, row.LeaseExpiresAt),
            null));
    }

    public Task<Guid?> TryTakeOverAsync(string op, string key, string hash, DateTimeOffset newLease, CancellationToken ct)
    {
        if (!FailTakeovers
            && _rows.TryGetValue((op, key), out Row? row)
            && row.Status == IdempotencyRecordStatus.InProgress
            && row.LeaseExpiresAt < newLease
            && row.RequestHash == hash)
        {
            var newToken = Guid.NewGuid();
            _rows[(op, key)] = row with { LeaseExpiresAt = newLease, ClaimToken = newToken };
            return Task.FromResult<Guid?>(newToken);
        }

        return Task.FromResult<Guid?>(null);
    }

    public Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken ct) =>
        Task.FromResult<IIdempotencyTransaction>(new FakeTransaction(this));

    public Task<bool> MarkFinishedAsync(string op, string key, Guid claimToken, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken ct)
    {
        if (!_rows.TryGetValue((op, key), out Row? row) || row.ClaimToken != claimToken)
        {
            return Task.FromResult(false);
        }

        _rows[(op, key)] = row with
        {
            Status = status, ResponseStatus = responseStatus, ResponseBody = responseBody,
        };
        return Task.FromResult(true);
    }

    public Task<bool> ReleaseClaimAsync(string op, string key, Guid claimToken, CancellationToken ct)
    {
        if (ThrowOnRelease)
        {
            throw new InvalidOperationException("Simulated store outage during release.");
        }

        if (!_rows.TryGetValue((op, key), out Row? row)
            || row.ClaimToken != claimToken
            || row.Status != IdempotencyRecordStatus.InProgress)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(_rows.TryRemove((op, key), out _));
    }

    private sealed class FakeTransaction : IIdempotencyTransaction
    {
        private readonly FakeIdempotencyStore _owner;
        public FakeTransaction(FakeIdempotencyStore owner) => _owner = owner;
        public Task CommitAsync(CancellationToken ct) { _owner.CommittedInTransaction = true; return Task.CompletedTask; }
        public Task RollbackAsync(CancellationToken ct) { _owner.LastTransactionRolledBack = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
