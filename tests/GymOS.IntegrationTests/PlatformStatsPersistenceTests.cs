using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>The daily read model round-tripping through PostgreSQL, including the write-once rule.</summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PlatformStatsPersistenceTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private PlatformStatsHost _host = null!;

    public PlatformStatsPersistenceTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("platform_stats");
        _host = new PlatformStatsHost(connectionString);

        await using AppDbContext context = _host.CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AStatRowRoundTripsWithItsPerCurrencyMrr()
    {
        await using (var write = _host.CreateDbContext())
        {
            write.Add(DailyPlatformStats.Record(
                new DateOnly(2026, 8, 10),
                new GymCounts(5, 3, 1, 1, 2, 1, 0),
                [Money.Of(120.50m, Currency.Of("EUR")), Money.Of(80m, Currency.Of("GBP"))],
                new DateTimeOffset(2026, 8, 11, 3, 30, 0, TimeSpan.Zero)));
            await write.SaveChangesAsync();
        }

        await using var read = _host.CreateDbContext();
        DailyPlatformStats stored = await read.Set<DailyPlatformStats>()
            .Include(stats => stats.Mrr)
            .SingleAsync(stats => stats.StatDate == new DateOnly(2026, 8, 10));

        Assert.Equal(3, stored.ActiveGyms);
        Assert.Equal(
            [Money.Of(120.50m, Currency.Of("EUR")), Money.Of(80m, Currency.Of("GBP"))],
            stored.Mrr.Select(mrr => mrr.Amount).OrderBy(amount => amount.Currency.Code).ToList());
    }

    [Fact]
    public async Task TheSameDayCannotBeRecordedTwice()
    {
        // The write-once rule's guarantee: the job skips a present day, and if a bug ever gets past
        // that check the database refuses it rather than growing a second version of history.
        await using var context = _host.CreateDbContext();
        context.Add(DailyPlatformStats.Record(new DateOnly(2026, 8, 9), new GymCounts(1, 1, 0, 0, 0, 0, 0), [], DateTimeOffset.UnixEpoch));
        await context.SaveChangesAsync();

        context.Add(DailyPlatformStats.Record(new DateOnly(2026, 8, 9), new GymCounts(2, 2, 0, 0, 0, 0, 0), [], DateTimeOffset.UnixEpoch));

        await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// A database-only stand-in for <c>AuthenticationHost</c>: this suite writes through the
    /// DbContext rather than the API, so it needs a connection string and a context factory, not a
    /// hosted server.
    /// </summary>
    private sealed class PlatformStatsHost
    {
        private readonly string _connectionString;

        public PlatformStatsHost(string connectionString) => _connectionString = connectionString;

        public AppDbContext CreateDbContext()
        {
            DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
                .UseSnakeCaseNamingConvention()
                .Options;

            return new AppDbContext(options);
        }
    }
}
