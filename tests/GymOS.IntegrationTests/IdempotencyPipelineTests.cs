using GymOS.BuildingBlocks.Application.Behaviors;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyPipelineTests : IAsyncLifetime
{
    public sealed record PaymentDto(Guid CommandId, int ExecutionNumber);

    public sealed record RecordPayment(Guid CommandId, bool FailBusiness) : ICommand<Result<PaymentDto>>, IIdempotentCommand
    {
        public string OperationKey => "test.record-payment";
        public TimeSpan RetentionPeriod => TimeSpan.FromHours(72);
        public bool KeyRequired => true;
    }

    public sealed class ExecutionCounter { private int _count; public int Increment() => Interlocked.Increment(ref _count); public int Count => _count; }

    public sealed class RecordPaymentHandler : ICommandHandler<RecordPayment, Result<PaymentDto>>
    {
        private readonly ExecutionCounter _counter;
        public RecordPaymentHandler(ExecutionCounter counter) => _counter = counter;

        public Task<Result<PaymentDto>> HandleAsync(RecordPayment command, CancellationToken cancellationToken)
        {
            int execution = _counter.Increment();
            return Task.FromResult(command.FailBusiness
                ? Result.Failure<PaymentDto>(Error.Conflict("payments.already_recorded", "Deterministic business failure."))
                : Result.Success(new PaymentDto(command.CommandId, execution)));
        }
    }

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;
    private readonly ExecutionCounter _counter = new();

    // Real wall-clock, NOT a FakeClock — the same reasoning as IdempotencyStoreTests: the store
    // compares expiry and lease columns against PostgreSQL's own now(), so a clock pinned in the
    // past writes records that the database judges by real time. A fixed clock here was a time
    // bomb: RetentionPeriod is 72h, and the moment real time passed fixed-time + 72h, every
    // stored outcome was already expired at birth ("expired behaves as absent") and nothing
    // replayed — these tests failed on every checkout, three days after the pinned date.
    private readonly SystemClock _clock = new();

    public IdempotencyPipelineTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_pipeline");
        // Migrations moved into PostgresFixture, which applies them as the migrator before handing
        // back a connection string: the app role every host connects as owns nothing and holds no DDL.
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Mirrors IdempotencyStoreTests.CreateContext's exact AppDbContext configuration
        // (MigrationsAssembly + snake_case convention), adapted to AddDbContext for DI.
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention());
        services.AddCqrsDispatcher();
        services.AddIdempotencyBehavior();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<ICommandHandler<RecordPayment, Result<PaymentDto>>, RecordPaymentHandler>();
        services.AddSingleton(_counter);
        services.AddSingleton<GymOS.BuildingBlocks.Application.Abstractions.IClock>(_clock);
        services.AddOptions<IdempotencyOptions>().Configure(o => o.LeaseDuration = TimeSpan.FromSeconds(60));
        return services.BuildServiceProvider();
    }

    private async Task<Result<PaymentDto>> DispatchAsync(RecordPayment command, string key)
    {
        await using ServiceProvider provider = BuildProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IdempotencyContext>().SetKey(key);
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .SendAsync(command, CancellationToken.None);
    }

    [Fact]
    public async Task Retry_with_the_same_key_executes_once_and_replays_the_same_body()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: false);
        int before = _counter.Count;

        Result<PaymentDto> first = await DispatchAsync(command, "pipe-replay");
        Result<PaymentDto> second = await DispatchAsync(command, "pipe-replay");

        Assert.Equal(before + 1, _counter.Count);
        Assert.Equal(first.Value, second.Value); // ExecutionNumber identical → body verbatim
    }

    [Fact]
    public async Task Same_key_different_command_rejects_key_reuse_mismatch()
    {
        await DispatchAsync(new RecordPayment(Guid.NewGuid(), false), "pipe-mismatch");
        Result<PaymentDto> second = await DispatchAsync(new RecordPayment(Guid.NewGuid(), false), "pipe-mismatch");

        Assert.True(second.IsFailure);
        Assert.Equal("idempotency.key_reuse_mismatch", second.Error!.Code);
    }

    [Fact]
    public async Task Deterministic_business_failure_replays_without_reexecuting()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: true);
        int before = _counter.Count;

        Result<PaymentDto> first = await DispatchAsync(command, "pipe-failed");
        Result<PaymentDto> second = await DispatchAsync(command, "pipe-failed");

        Assert.Equal(before + 1, _counter.Count);
        Assert.Equal("payments.already_recorded", first.Error!.Code);
        Assert.Equal("payments.already_recorded", second.Error!.Code);
    }

    [Fact]
    public async Task Concurrent_duplicates_execute_exactly_once()
    {
        var command = new RecordPayment(Guid.NewGuid(), FailBusiness: false);
        int before = _counter.Count;

        Result<PaymentDto>[] results = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(_ => DispatchAsync(command, "pipe-race")));

        Assert.Equal(before + 1, _counter.Count);
        foreach (Result<PaymentDto> result in results)
        {
            // Every caller got either the (replayed) success or the in-flight conflict — never a second execution.
            if (result.IsFailure)
            {
                Assert.Equal("idempotency.operation_in_progress", result.Error!.Code);
            }
            else
            {
                Assert.Equal(before + 1, result.Value.ExecutionNumber);
            }
        }
        Assert.Contains(results, result => result.IsSuccess);
    }
}
