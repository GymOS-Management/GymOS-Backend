using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The whole recording path on real SQL Server: a real <see cref="JobHost"/>, the real
/// <see cref="SqlServerJobLock"/>, the real <see cref="JobRunRecorder"/>, and a test job — because
/// the row's shape (status conversion, durations, the trimmed error) is what the console reads, and
/// a fake recorder would only prove the fake.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class JobRunRecordingTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public JobRunRecordingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync() =>
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("job_run_recording");

    public Task DisposeAsync() => _sqlServer.DropDatabaseAsync(_connectionString);

    private sealed class SignallingJob : IScheduledJob
    {
        private readonly TaskCompletionSource _ran = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Fail { get; init; }

        public Task Ran => _ran.Task;

        public string Name => "test.signalling";

        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMilliseconds(50));

        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            _ran.TrySetResult();
            return Fail ? throw new InvalidOperationException("deliberate") : Task.CompletedTask;
        }
    }

    private (JobHost Host, SignallingJob Job) BuildHost(bool fail)
    {
        var job = new SignallingJob { Fail = fail };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(options => options
            .UseSqlServer(_connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention());
        services.AddSingleton<IClock>(new SystemClock());
        services.AddScoped<IJobLock, SqlServerJobLock>();
        services.AddScoped<IJobRunRecorder, JobRunRecorder>();
        services.AddSingleton(job);
        services.AddScheduledJob<SignallingJob>();
        ServiceProvider provider = services.BuildServiceProvider();

        var host = new JobHost(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IClock>(),
            Options.Create(new JobHostOptions()),
            NullLogger<JobHost>.Instance);

        return (host, job);
    }

    private async Task<JobRun> WaitForFinishedRunAsync()
    {
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(_connectionString, TestDatabase.Options)
            .UseSnakeCaseNamingConvention()
            .Options;

        DateTimeOffset deadline = Clock.UtcNow + Timeout;
        while (Clock.UtcNow < deadline)
        {
            await using var context = new AppDbContext(options);
            JobRun? run = await context.Set<JobRun>().AsNoTracking()
                .Where(r => r.Status != JobRunStatus.Running)
                .OrderBy(r => r.StartedAtUtc)
                .FirstOrDefaultAsync();
            if (run is not null)
            {
                return run;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("No finished job run was recorded.");
    }

    [Fact]
    public async Task ASuccessfulRunIsRecordedWithDurationAndInstance()
    {
        (JobHost host, SignallingJob job) = BuildHost(fail: false);

        await host.StartAsync(CancellationToken.None);
        await job.Ran.WaitAsync(Timeout);
        JobRun run = await WaitForFinishedRunAsync();
        await host.StopAsync(CancellationToken.None);

        Assert.Equal("test.signalling", run.JobName);
        Assert.Equal(JobRunStatus.Succeeded, run.Status);
        Assert.Equal(Environment.MachineName, run.Instance);
        Assert.NotNull(run.FinishedAtUtc);
        Assert.NotNull(run.DurationMs);
        Assert.True(run.DurationMs >= 0);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task AFailingRunIsRecordedWithTheTrimmedErrorAndTheLoopKeepsTicking()
    {
        (JobHost host, SignallingJob job) = BuildHost(fail: true);

        await host.StartAsync(CancellationToken.None);
        await job.Ran.WaitAsync(Timeout);
        JobRun run = await WaitForFinishedRunAsync();
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(JobRunStatus.Failed, run.Status);
        Assert.Equal("InvalidOperationException: deliberate", run.Error);
    }
}
