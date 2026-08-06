using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyStoreTests : IAsyncLifetime
{
    // TryTakeOverAsync's lease check runs against PostgreSQL's own now(), so the test needs
    // real wall-clock time, not a fixed instant — SystemClock is the sanctioned call site
    // (AD-40); Application.Idempotency stays clock-free in production.
    private static readonly SystemClock Clock = new();

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public IdempotencyStoreTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_store");
        await using AppDbContext context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    private static IdempotencyClaimRequest Claim(string key, string hash = "a1") => new(
        OperationKey: "test.op",
        IdempotencyKey: key,
        RequestHash: hash.PadRight(64, '0'),
        LeaseExpiresAt: Clock.UtcNow.AddMinutes(1),
        CreatedAt: Clock.UtcNow,
        ExpiresAt: Clock.UtcNow.AddHours(24));

    [Fact]
    public async Task First_claim_wins_second_returns_existing_snapshot()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        IdempotencyClaimResult first = await store.TryClaimAsync(Claim("k1"), CancellationToken.None);
        IdempotencyClaimResult second = await store.TryClaimAsync(Claim("k1"), CancellationToken.None);

        Assert.True(first.Claimed);
        Assert.False(second.Claimed);
        Assert.Equal(IdempotencyRecordStatus.InProgress, second.Existing!.Status);
    }

    [Fact]
    public async Task Parallel_claims_for_the_same_key_yield_exactly_one_winner()
    {
        IdempotencyClaimResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(async _ =>
            {
                await using AppDbContext context = CreateContext();
                return await new IdempotencyStore(context).TryClaimAsync(Claim("k-race"), CancellationToken.None);
            }));

        Assert.Equal(1, results.Count(result => result.Claimed));
    }

    [Fact]
    public async Task MarkFinished_inside_a_rolled_back_transaction_leaves_the_record_InProgress()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);
        await store.TryClaimAsync(Claim("k-rollback"), CancellationToken.None);

        await using (IIdempotencyTransaction transaction =
            await store.BeginTransactionAsync(CancellationToken.None))
        {
            await store.MarkFinishedAsync("test.op", "k-rollback",
                IdempotencyRecordStatus.Completed, 0, """{"ok":true}""", CancellationToken.None);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using AppDbContext fresh = CreateContext();
        IdempotencyClaimResult after = await new IdempotencyStore(fresh)
            .TryClaimAsync(Claim("k-rollback"), CancellationToken.None);
        Assert.Equal(IdempotencyRecordStatus.InProgress, after.Existing!.Status);
    }

    [Fact]
    public async Task TakeOver_succeeds_only_when_lease_expired_and_hash_matches()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        IdempotencyClaimRequest expired = Claim("k-lease") with { LeaseExpiresAt = Clock.UtcNow.AddMinutes(-1) };
        await store.TryClaimAsync(expired, CancellationToken.None);

        bool wrongHash = await store.TryTakeOverAsync("test.op", "k-lease",
            "ff".PadRight(64, '0'), Clock.UtcNow.AddMinutes(1), CancellationToken.None);
        bool rightHash = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, Clock.UtcNow.AddMinutes(1), CancellationToken.None);
        bool secondTime = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, Clock.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.False(wrongHash);
        Assert.True(rightHash);
        Assert.False(secondTime); // lease is live again after the first takeover
    }

    [Fact]
    public async Task Release_deletes_the_claim_so_a_retry_reexecutes()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);
        await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);

        await store.ReleaseClaimAsync("test.op", "k-release", CancellationToken.None);

        IdempotencyClaimResult again = await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);
        Assert.True(again.Claimed);
    }

    [Fact]
    public async Task Claim_unique_index_exists_with_coalesced_tenant()
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE schemaname='platform' AND indexname='ux_idempotency_records_claim'",
            connection);
        string? definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("COALESCE", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UNIQUE", definition, StringComparison.OrdinalIgnoreCase);
    }
}
