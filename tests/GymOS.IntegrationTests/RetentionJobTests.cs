using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The retention job performs both folded maintenance halves against real PostgreSQL: expired
/// idempotency records go, and audit partitions exist ahead of need. Real wall clock, like the
/// other idempotency tests — the rows compare against database time.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class RetentionJobTests : IAsyncLifetime
{
    private static readonly SystemClock Clock = new();

    private readonly PostgresFixture _postgres;
    private string _connectionString = null!;

    public RetentionJobTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _connectionString = await _postgres.CreateIsolatedDatabaseAsync("retention_job");
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

    [Fact]
    public async Task One_run_purges_expired_records_and_ensures_partitions()
    {
        await using AppDbContext context = CreateContext();

        // One record expired an hour ago, one still live for a day.
        DateTimeOffset now = Clock.UtcNow;
        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO platform.idempotency_records
                (id, operation_key, idempotency_key, request_hash, status, response_status,
                 claim_token, lease_expires_at, created_at, expires_at)
            VALUES
                (gen_random_uuid(), 'test.op', 'expired', '{new string('a', 64)}', 2, 200,
                 gen_random_uuid(), {now.AddHours(-2)}, {now.AddHours(-3)}, {now.AddHours(-1)}),
                (gen_random_uuid(), 'test.op', 'live', '{new string('b', 64)}', 2, 200,
                 gen_random_uuid(), {now.AddHours(-2)}, {now.AddHours(-3)}, {now.AddDays(1)})
            """);

        var job = new RetentionJob(
            new AuditLogPartitionMaintainer(
                context,
                Clock,
                Options.Create(new AuditLogPartitionOptions()),
                NullLogger<AuditLogPartitionMaintainer>.Instance),
            new IdempotencyRecordMaintainer(
                context, Clock, NullLogger<IdempotencyRecordMaintainer>.Instance),
            new RefreshTokenMaintainer(
                context, Clock, NullLogger<RefreshTokenMaintainer>.Instance),
            new ActivationTokenMaintainer(
                context, Clock, NullLogger<ActivationTokenMaintainer>.Instance));

        await job.ExecuteAsync(Clock.UtcNow, CancellationToken.None);

        List<string> remainingKeys = await context.Database
            .SqlQueryRaw<string>("""SELECT idempotency_key AS "Value" FROM platform.idempotency_records""")
            .ToListAsync();
        Assert.Equal(["live"], remainingKeys);

        // Partitions exist for next month — the recurring guarantee the startup call could only
        // give processes that restart often enough.
        DateTime nextMonth = Clock.UtcNow.UtcDateTime.AddMonths(1);
        string expectedPartition = $"audit_logs_{nextMonth:yyyy_MM}";
        List<string> partitions = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT tablename AS "Value" FROM pg_tables
                WHERE schemaname = 'platform' AND tablename LIKE 'audit_logs_%'
                """)
            .ToListAsync();
        Assert.Contains(expectedPartition, partitions);

        // Idempotent: a second run finds nothing left to do and changes nothing.
        await job.ExecuteAsync(Clock.UtcNow, CancellationToken.None);
        Assert.Equal(1, await context.Database
            .SqlQueryRaw<int>("""SELECT COUNT(*)::int AS "Value" FROM platform.idempotency_records""")
            .SingleAsync());
    }
}
