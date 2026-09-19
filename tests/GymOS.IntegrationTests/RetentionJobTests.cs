using System.Globalization;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The retention job performs both folded maintenance halves against real SQL Server: expired
/// idempotency records go, and audit partitions exist ahead of need. Real wall clock, like the
/// other idempotency tests — the rows compare against database time.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class RetentionJobTests : IAsyncLifetime
{
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public RetentionJobTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("retention_job");
        // Migrations moved into SqlServerFixture, which applies them as the migrator before handing
        // back a connection string: the app role every host connects as owns nothing and holds no DDL.
    }

    public Task DisposeAsync() => _sqlServer.DropDatabaseAsync(_connectionString);

    private AppDbContext CreateContext()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, TestDatabase.Options)
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
                (NEWID(), 'test.op', 'expired', '{new string('a', 64)}', 2, 200,
                 NEWID(), {now.AddHours(-2)}, {now.AddHours(-3)}, {now.AddHours(-1)}),
                (NEWID(), 'test.op', 'live', '{new string('b', 64)}', 2, 200,
                 NEWID(), {now.AddHours(-2)}, {now.AddHours(-3)}, {now.AddDays(1)})
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
                context, Clock, NullLogger<ActivationTokenMaintainer>.Instance),
            new JobRunMaintainer(
                context, Clock, new FakeSettings<MonitoringOptions>(new MonitoringOptions()), NullLogger<JobRunMaintainer>.Instance));

        await job.ExecuteAsync(Clock.UtcNow, CancellationToken.None);

        List<string> remainingKeys = await context.Database
            .SqlQueryRaw<string>("""SELECT idempotency_key AS "Value" FROM platform.idempotency_records""")
            .ToListAsync();
        Assert.Equal(["live"], remainingKeys);

        // Partitions exist for next month — the recurring guarantee the startup call could only
        // give processes that restart often enough.
        // A boundary, not a table: SQL Server partitions are numbered ranges of one table rather
        // than separate tables, so what maintenance creates is a new split point. See
        // AuditLogPartitioningTests for the same substitution across that whole suite.
        DateTime nextMonth = Clock.UtcNow.UtcDateTime.AddMonths(1);
        string expectedBoundary = nextMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        List<string> boundaries = await context.Database
            .SqlQueryRaw<string>(
                """
                SELECT CONVERT(char(7), CONVERT(datetimeoffset(7), prv.value), 126) AS [Value]
                FROM sys.partition_range_values prv
                JOIN sys.partition_functions pf ON pf.function_id = prv.function_id
                WHERE pf.name = N'pf_audit_logs_monthly'
                """)
            .ToListAsync();
        Assert.Contains(expectedBoundary, boundaries);

        // Idempotent: a second run finds nothing left to do and changes nothing.
        await job.ExecuteAsync(Clock.UtcNow, CancellationToken.None);
        Assert.Equal(1, await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM platform.idempotency_records")
            .SingleAsync());
    }
}
