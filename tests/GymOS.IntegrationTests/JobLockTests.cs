using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The job lock against real SQL Server: one holder per name, independent names do not contend,
/// and release makes the name acquirable again — the property that lets two API replicas run the
/// job host without ever running the same sweep twice.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class JobLockTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public JobLockTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync() =>
        // No migration: advisory locks live in the server, not the schema.
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("job_lock");

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task One_holder_per_name_until_released()
    {
        await using AppDbContext first = CreateContext();
        await using AppDbContext second = CreateContext();
        var firstLock = new SqlServerJobLock(first);
        var secondLock = new SqlServerJobLock(second);

        IAsyncDisposable? held = await firstLock.TryAcquireAsync("renewal", CancellationToken.None);
        Assert.NotNull(held);

        // Same name, different session: refused, not queued — the loser skips its tick.
        Assert.Null(await secondLock.TryAcquireAsync("renewal", CancellationToken.None));

        // A different job's name is a different lock entirely.
        IAsyncDisposable? other = await secondLock.TryAcquireAsync("retention", CancellationToken.None);
        Assert.NotNull(other);
        await other!.DisposeAsync();

        // Release, and the name is free for the next instance.
        await held!.DisposeAsync();
        IAsyncDisposable? reacquired = await secondLock.TryAcquireAsync("renewal", CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }

    [Fact]
    public async Task A_droppedConnectionReleasesTheLock()
    {
        var holder = CreateContext();
        IAsyncDisposable? held = await new SqlServerJobLock(holder).TryAcquireAsync(
            "crash-test", CancellationToken.None);
        Assert.NotNull(held);

        // Simulate a crashed instance: the context (and its session) dies without a polite
        // unlock. SQL Server frees session application locks with the session — no job can be wedged shut.
        await holder.DisposeAsync();

        await using AppDbContext survivor = CreateContext();
        IAsyncDisposable? reacquired = await new SqlServerJobLock(survivor).TryAcquireAsync(
            "crash-test", CancellationToken.None);
        Assert.NotNull(reacquired);
        await reacquired!.DisposeAsync();
    }
}
