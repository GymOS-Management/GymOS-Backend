using System.Collections.Concurrent;
using GymOS.BuildingBlocks.Application.Idempotency;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

/// <summary>In-memory IIdempotencyStore mirroring the real store's observable semantics.</summary>
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private sealed record Row(string RequestHash, IdempotencyRecordStatus Status,
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
        _rows[(op, key)] = new Row(hash, IdempotencyRecordStatus.InProgress, 0, null, leaseExpiresAt);

    public Task<IdempotencyClaimResult> TryClaimAsync(IdempotencyClaimRequest request, CancellationToken cancellationToken)
    {
        bool claimed = _rows.TryAdd(
            (request.OperationKey, request.IdempotencyKey),
            new Row(request.RequestHash, IdempotencyRecordStatus.InProgress, 0, null, request.LeaseExpiresAt));

        if (claimed)
        {
            return Task.FromResult(new IdempotencyClaimResult(true, null));
        }

        Row row = _rows[(request.OperationKey, request.IdempotencyKey)];
        return Task.FromResult(new IdempotencyClaimResult(false,
            new IdempotencyRecordSnapshot(row.Status, row.RequestHash, row.ResponseStatus, row.ResponseBody, row.LeaseExpiresAt)));
    }

    public Task<bool> TryTakeOverAsync(string op, string key, string hash, DateTimeOffset newLease, CancellationToken ct)
    {
        if (_rows.TryGetValue((op, key), out Row? row)
            && row.Status == IdempotencyRecordStatus.InProgress
            && row.LeaseExpiresAt < newLease
            && row.RequestHash == hash)
        {
            _rows[(op, key)] = row with { LeaseExpiresAt = newLease };
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<IIdempotencyTransaction> BeginTransactionAsync(CancellationToken ct) =>
        Task.FromResult<IIdempotencyTransaction>(new FakeTransaction(this));

    public Task MarkFinishedAsync(string op, string key, IdempotencyRecordStatus status,
        int responseStatus, string? responseBody, CancellationToken ct)
    {
        _rows[(op, key)] = _rows[(op, key)] with
        {
            Status = status, ResponseStatus = responseStatus, ResponseBody = responseBody,
        };
        return Task.CompletedTask;
    }

    public Task ReleaseClaimAsync(string op, string key, CancellationToken ct)
    {
        _rows.TryRemove((op, key), out _);
        return Task.CompletedTask;
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
