using GymOS.BuildingBlocks.Application.Behaviors;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Idempotency;

public sealed class IdempotencyBehaviorTests
{
    private sealed record PaymentDto(string Reference);

    private sealed record MarkPaid(Guid InvoiceId) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.mark-paid";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(24);
        public bool KeyRequired => true;
    }

    private sealed record OptionalKey(Guid Id) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.optional";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(24);
        public bool KeyRequired => false;
    }

    private sealed record NotIdempotent(Guid Id) : ICommand<Result<PaymentDto>>;

    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeIdempotencyStore _store = new();
    private readonly IdempotencyContext _context = new();
    private int _executions;

    private IdempotencyBehavior<TRequest, Result<PaymentDto>> Behavior<TRequest>()
        where TRequest : notnull =>
        new(_store, _context, _clock,
            Options.Create(new IdempotencyOptions { LeaseDuration = TimeSpan.FromSeconds(60) }),
            NullLogger<IdempotencyBehavior<TRequest, Result<PaymentDto>>>.Instance);

    private Task<Result<PaymentDto>> Handler()
    {
        _executions++;
        return Task.FromResult(Result.Success(new PaymentDto("PAY-1")));
    }

    // Row 1: no record → execute, claim first.
    [Fact]
    public async Task Fresh_key_executes_and_stores_completed()
    {
        _context.SetKey("k1");
        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
        Assert.Equal(IdempotencyRecordStatus.Completed, _store.Single("test.mark-paid", "k1").Status);
        Assert.True(_store.CommittedInTransaction);
    }

    // Row 2: Completed + same hash → replay, handler does NOT run.
    [Fact]
    public async Task Completed_same_hash_replays_without_executing()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        await Behavior<MarkPaid>().HandleAsync(command, Handler, CancellationToken.None);

        Result<PaymentDto> replay = await Behavior<MarkPaid>()
            .HandleAsync(command, Handler, CancellationToken.None);

        Assert.Equal(1, _executions);
        Assert.True(replay.IsSuccess);
        Assert.Equal("PAY-1", replay.Value.Reference);
        Assert.True(_context.WasReplayed);
    }

    // Row 3: Completed + different hash → key_reuse_mismatch.
    [Fact]
    public async Task Completed_different_hash_rejects_key_reuse()
    {
        _context.SetKey("k1");
        await Behavior<MarkPaid>().HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Result<PaymentDto> second = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(second.IsFailure);
        Assert.Equal("idempotency.key_reuse_mismatch", second.Error!.Code);
        Assert.Equal(1, _executions);
    }

    // Row 4: InProgress + live lease → operation_in_progress with RetryAfter.
    [Fact]
    public async Task InProgress_with_live_lease_rejects_conflict_with_retry_after()
    {
        _store.SeedInProgress("test.mark-paid", "k1",
            HashOf(new MarkPaid(WellKnownId)), _clock.UtcNow.AddSeconds(45));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("idempotency.operation_in_progress", result.Error!.Code);
        Assert.Equal(0, _executions);
        Assert.NotNull(_context.RetryAfter);
        Assert.InRange(_context.RetryAfter!.Value.TotalSeconds, 1, 45);
    }

    // Row 5: InProgress + expired lease + same hash → take over and execute.
    [Fact]
    public async Task Expired_lease_same_hash_takes_over_and_executes()
    {
        _store.SeedInProgress("test.mark-paid", "k1",
            HashOf(new MarkPaid(WellKnownId)), _clock.UtcNow.AddSeconds(-5));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
    }

    // Row 5b: expired lease + different hash is still key reuse.
    [Fact]
    public async Task Expired_lease_different_hash_rejects_key_reuse()
    {
        _store.SeedInProgress("test.mark-paid", "k1", new string('f', 64), _clock.UtcNow.AddSeconds(-5));
        _context.SetKey("k1");

        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(WellKnownId), Handler, CancellationToken.None);

        Assert.Equal("idempotency.key_reuse_mismatch", result.Error!.Code);
        Assert.Equal(0, _executions);
    }

    // Row 6: FailedDeterministic + same hash → replay the stored business error.
    [Fact]
    public async Task Deterministic_failure_replays_without_reexecuting()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        int calls = 0;
        Task<Result<PaymentDto>> FailingHandler()
        {
            calls++;
            return Task.FromResult(Result.Failure<PaymentDto>(
                Error.Conflict("invoices.already_paid", "Already paid.")));
        }

        Result<PaymentDto> first = await Behavior<MarkPaid>().HandleAsync(command, FailingHandler, CancellationToken.None);
        Result<PaymentDto> second = await Behavior<MarkPaid>().HandleAsync(command, FailingHandler, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("invoices.already_paid", first.Error!.Code);
        Assert.Equal("invoices.already_paid", second.Error!.Code);
        Assert.Equal(IdempotencyRecordStatus.FailedDeterministic, _store.Single("test.mark-paid", "k1").Status);
    }

    // Row 7: transient failure (exception) → claim released, retry re-executes.
    [Fact]
    public async Task Exception_releases_claim_and_a_retry_reexecutes()
    {
        var command = new MarkPaid(Guid.NewGuid());
        _context.SetKey("k1");
        static Task<Result<PaymentDto>> Throwing() => throw new TimeoutException("db down");

        await Assert.ThrowsAsync<TimeoutException>(() =>
            Behavior<MarkPaid>().HandleAsync(command, Throwing, CancellationToken.None));
        Assert.True(_store.IsEmpty);
        Assert.True(_store.LastTransactionRolledBack);

        Result<PaymentDto> retry = await Behavior<MarkPaid>().HandleAsync(command, Handler, CancellationToken.None);
        Assert.True(retry.IsSuccess);
        Assert.Equal(1, _executions);
    }

    // Gate: KeyRequired without a header → 400 error, nothing executes, nothing stored.
    [Fact]
    public async Task Required_key_missing_rejects_400_without_executing()
    {
        Result<PaymentDto> result = await Behavior<MarkPaid>()
            .HandleAsync(new MarkPaid(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.Equal("idempotency.key_required", result.Error!.Code);
        Assert.Equal(0, _executions);
        Assert.True(_store.IsEmpty);
    }

    // Gate: optional key + no header → executes unprotected.
    [Fact]
    public async Task Optional_key_missing_executes_without_a_record()
    {
        Result<PaymentDto> result = await Behavior<OptionalKey>()
            .HandleAsync(new OptionalKey(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, _executions);
        Assert.True(_store.IsEmpty);
    }

    // Gate: non-idempotent commands pass straight through.
    [Fact]
    public async Task Non_idempotent_command_is_untouched()
    {
        _context.SetKey("k1"); // even with a header present
        Result<PaymentDto> result = await Behavior<NotIdempotent>()
            .HandleAsync(new NotIdempotent(Guid.NewGuid()), Handler, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(_store.IsEmpty);
    }

    private static readonly Guid WellKnownId = new("11111111-1111-1111-1111-111111111111");

    private static string HashOf(object command) => CanonicalRequestHasher.Hash(command);
}
