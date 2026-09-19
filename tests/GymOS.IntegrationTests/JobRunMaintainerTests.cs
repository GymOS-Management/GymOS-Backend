using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.TestingSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>The retention sweep over real rows: the window applies, and the newest run of every job survives however old.</summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class JobRunMaintainerTests : IAsyncLifetime
{
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public JobRunMaintainerTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync() =>
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("job_run_maintainer");

    public Task DisposeAsync() => _sqlServer.DropDatabaseAsync(_connectionString);

    private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(_connectionString, TestDatabase.Options)
        .UseSnakeCaseNamingConvention()
        .Options);

    private async Task SeedAsync(string jobName, int daysAgo)
    {
        await using AppDbContext context = CreateContext();
        context.Set<JobRun>().Add(JobRun.Start(Guid.NewGuid(), jobName, "seed", Clock.UtcNow.AddDays(-daysAgo)));
        await context.SaveChangesAsync();
    }

    private async Task<List<int>> RemainingAgesAsync(string jobName)
    {
        await using AppDbContext context = CreateContext();
        List<DateTimeOffset> starts = await context.Set<JobRun>().AsNoTracking()
            .Where(run => run.JobName == jobName).Select(run => run.StartedAtUtc).ToListAsync();
        return starts.Select(start => (int)Math.Round((Clock.UtcNow - start).TotalDays)).OrderBy(age => age).ToList();
    }

    [Fact]
    public async Task ItRemovesRunsPastTheWindowAndKeepsTheNewestOfEveryJob()
    {
        await SeedAsync("renewal", 31);
        await SeedAsync("renewal", 29);
        await SeedAsync("renewal", 1);
        await SeedAsync("rare", 400); // its job's only run

        await using AppDbContext context = CreateContext();
        var maintainer = new JobRunMaintainer(
            context, Clock, new FakeSettings<MonitoringOptions>(new MonitoringOptions { JobRunRetentionDays = 30 }),
            NullLogger<JobRunMaintainer>.Instance);

        await maintainer.PurgeAsync(CancellationToken.None);

        Assert.Equal([1, 29], await RemainingAgesAsync("renewal"));
        Assert.Equal([400], await RemainingAgesAsync("rare"));
    }

    [Fact]
    public async Task ASecondSweepChangesNothing()
    {
        await SeedAsync("renewal", 31);
        await SeedAsync("renewal", 1);

        await using AppDbContext context = CreateContext();
        var maintainer = new JobRunMaintainer(
            context, Clock, new FakeSettings<MonitoringOptions>(new MonitoringOptions()), NullLogger<JobRunMaintainer>.Instance);

        await maintainer.PurgeAsync(CancellationToken.None);
        await maintainer.PurgeAsync(CancellationToken.None);

        Assert.Equal([1], await RemainingAgesAsync("renewal"));
    }
}
