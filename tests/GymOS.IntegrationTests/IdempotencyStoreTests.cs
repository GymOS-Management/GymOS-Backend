using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(SqlServerCollectionDefinition.Name)]
public sealed class IdempotencyStoreTests : IAsyncLifetime
{
    // TryTakeOverAsync's lease check runs against the database's own clock (SYSDATETIMEOFFSET), so the test needs
    // real wall-clock time, not a fixed instant — SystemClock is the sanctioned call site
    // (AD-40); Application.Idempotency stays clock-free in production.
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public IdempotencyStoreTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("idempotency_store");
        // Migrations moved into SqlServerFixture, which applies them as the migrator before handing
        // back a connection string: the app role every host connects as owns nothing and holds no DDL.
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, TestDatabase.Options)
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
        Assert.NotNull(first.ClaimToken);
        Assert.False(second.Claimed);
        Assert.Null(second.ClaimToken);
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
        IdempotencyClaimResult claim = await store.TryClaimAsync(Claim("k-rollback"), CancellationToken.None);

        await using (IIdempotencyTransaction transaction =
            await store.BeginTransactionAsync(CancellationToken.None))
        {
            await store.MarkFinishedAsync("test.op", "k-rollback", claim.ClaimToken!.Value,
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
        IdempotencyClaimResult claim = await store.TryClaimAsync(expired, CancellationToken.None);

        Guid? wrongHash = await store.TryTakeOverAsync("test.op", "k-lease",
            "ff".PadRight(64, '0'), Clock.UtcNow.AddMinutes(1), CancellationToken.None);
        Guid? rightHash = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, Clock.UtcNow.AddMinutes(1), CancellationToken.None);
        Guid? secondTime = await store.TryTakeOverAsync("test.op", "k-lease",
            expired.RequestHash, Clock.UtcNow.AddMinutes(1), CancellationToken.None);

        Assert.Null(wrongHash);
        Assert.NotNull(rightHash);
        Assert.NotEqual(claim.ClaimToken!.Value, rightHash!.Value); // takeover fences with a NEW token
        Assert.Null(secondTime); // lease is live again after the first takeover
    }

    [Fact]
    public async Task Release_deletes_the_claim_so_a_retry_reexecutes()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);
        IdempotencyClaimResult claim = await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);

        bool released = await store.ReleaseClaimAsync(
            "test.op", "k-release", claim.ClaimToken!.Value, CancellationToken.None);

        Assert.True(released);
        IdempotencyClaimResult again = await store.TryClaimAsync(Claim("k-release"), CancellationToken.None);
        Assert.True(again.Claimed);
    }

    [Fact]
    public async Task Stale_token_cannot_mark_finished_or_release_after_a_takeover()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        // A claims with token T1 and stalls past its lease.
        IdempotencyClaimRequest expired = Claim("k-fence") with { LeaseExpiresAt = Clock.UtcNow.AddMinutes(-1) };
        IdempotencyClaimResult claim = await store.TryClaimAsync(expired, CancellationToken.None);
        Guid staleToken = claim.ClaimToken!.Value;

        // B takes over: the row now carries token T2.
        Guid? newToken = await store.TryTakeOverAsync("test.op", "k-fence",
            expired.RequestHash, Clock.UtcNow.AddMinutes(1), CancellationToken.None);
        Assert.NotNull(newToken);

        // A wakes and tries to record its outcome with T1 — the fence blocks it.
        bool marked = await store.MarkFinishedAsync("test.op", "k-fence", staleToken,
            IdempotencyRecordStatus.Completed, 0, """{"stale":true}""", CancellationToken.None);
        Assert.False(marked);

        // A's failure path with T1 must not delete B's live claim either.
        bool released = await store.ReleaseClaimAsync("test.op", "k-fence", staleToken, CancellationToken.None);
        Assert.False(released);

        // The record still holds B's state: InProgress, no stale outcome, and B's token still works.
        await using AppDbContext fresh = CreateContext();
        var freshStore = new IdempotencyStore(fresh);
        IdempotencyClaimResult after = await freshStore.TryClaimAsync(Claim("k-fence"), CancellationToken.None);
        Assert.False(after.Claimed);
        Assert.Equal(IdempotencyRecordStatus.InProgress, after.Existing!.Status);
        Assert.Null(after.Existing.ResponseBody);

        bool markedByOwner = await freshStore.MarkFinishedAsync("test.op", "k-fence", newToken!.Value,
            IdempotencyRecordStatus.Completed, 0, """{"owner":true}""", CancellationToken.None);
        Assert.True(markedByOwner);
    }

    [Fact]
    public async Task An_expired_record_behaves_as_absent_so_a_new_claim_wins()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        // A Completed record whose retention has already elapsed (expires_at in the past).
        IdempotencyClaimRequest expiredRetention = Claim("k-expired") with
        {
            ExpiresAt = Clock.UtcNow.AddMinutes(-1),
        };
        IdempotencyClaimResult first = await store.TryClaimAsync(expiredRetention, CancellationToken.None);
        Assert.True(await store.MarkFinishedAsync("test.op", "k-expired", first.ClaimToken!.Value,
            IdempotencyRecordStatus.Completed, 0, """{"old":true}""", CancellationToken.None));

        // AD-39: an expired key behaves as a brand-new request — not a replay of the stale outcome.
        IdempotencyClaimResult again = await store.TryClaimAsync(Claim("k-expired"), CancellationToken.None);

        Assert.True(again.Claimed);
        Assert.NotNull(again.ClaimToken);
    }

    [Fact]
    public async Task Claim_unique_index_exists_with_coalesced_tenant()
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(TestDatabase.IndexDefinitionSql, connection);
        command.Parameters.AddWithValue("@schema", "platform");
        command.Parameters.AddWithValue("@table", "idempotency_records");
        command.Parameters.AddWithValue("@index", "ux_idempotency_records_claim");

        string? definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("UNIQUE", definition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tenant_id", definition, StringComparison.OrdinalIgnoreCase);

        // Not filtered. EF adds `WHERE [tenant_id] IS NOT NULL` to unique indexes over nullable
        // columns on its own; this one is hand-written so it does not, and asserting the absence is
        // what would catch someone "tidying" it into a model declaration. A filtered index here would
        // exempt every platform-scope claim from the constraint.
        Assert.DoesNotContain("WHERE", definition, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The property the index exists for, asserted through behaviour rather than through its text.
    /// </summary>
    /// <remarks>
    /// The PostgreSQL index read <c>COALESCE(tenant_id, '000…')</c> and the old version of the test
    /// above asserted that word appeared in its definition. The COALESCE was there for one reason —
    /// PostgreSQL treats two NULLs as distinct in a unique index, so platform-scope claims would not
    /// have collided without a sentinel — and SQL Server treats them as equal, so the sentinel is
    /// gone. That makes "does the definition mention COALESCE" the wrong question, and this the right
    /// one: two platform-scope claims on the same key must still conflict.
    /// </remarks>
    [Fact]
    public async Task Two_platform_scope_claims_on_the_same_key_conflict()
    {
        await using AppDbContext context = CreateContext();
        var store = new IdempotencyStore(context);

        // tenant_id is NULL on both — every claim this suite makes is platform-scope, which is
        // precisely the case PostgreSQL needed the COALESCE sentinel to catch.
        IdempotencyClaimResult first = await store.TryClaimAsync(Claim("shared-key"), CancellationToken.None);
        IdempotencyClaimResult second = await store.TryClaimAsync(Claim("shared-key"), CancellationToken.None);

        Assert.True(first.Claimed);
        Assert.False(second.Claimed);
        Assert.NotNull(second.Existing);
    }
}
