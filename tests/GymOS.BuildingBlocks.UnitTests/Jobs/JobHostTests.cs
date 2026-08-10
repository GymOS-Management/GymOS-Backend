using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

/// <summary>
/// The host's contract: due jobs run in fresh scopes, a held lock skips the tick, a failing run
/// never kills the loop, and a disabled host runs nothing. Schedule arithmetic itself is
/// <see cref="JobScheduleTests"/>' business.
/// </summary>
public sealed class JobHostTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class CountingJob : IScheduledJob
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public int RunsBeforeSignal { get; init; } = 2;

        public bool FailFirstRun { get; init; }

        public int Runs => _runs;

        public Task Reached => _reached.Task;

        public string Name => "test.counting";

        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMilliseconds(20));

        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            int run = Interlocked.Increment(ref _runs);

            if (run >= RunsBeforeSignal)
            {
                _reached.TrySetResult();
            }

            return FailFirstRun && run == 1
                ? throw new InvalidOperationException("First run fails by design.")
                : Task.CompletedTask;
        }
    }

    private sealed class DenyingLock : IJobLock
    {
        private readonly TaskCompletionSource _attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public Task Attempted => _attempted.Task;

        public Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) >= 2)
            {
                _attempted.TrySetResult();
            }

            return Task.FromResult<IAsyncDisposable?>(null);
        }
    }

    private sealed class GrantingLock : IJobLock
    {
        private sealed class Handle : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable?>(new Handle());
    }

    private static JobHost Host(CountingJob job, IJobLock jobLock, bool enabled = true)
    {
        var services = new ServiceCollection();
        services.AddSingleton(job);
        services.AddSingleton(jobLock);
        services.AddScheduledJob<CountingJob>(); // TryAddScoped defers to the singleton above
        services.AddSingleton<IClock>(new FakeClock(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));

        ServiceProvider provider = services.BuildServiceProvider();

        return new JobHost(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IClock>(),
            Options.Create(new JobHostOptions { Enabled = enabled }),
            NullLogger<JobHost>.Instance);
    }

    [Fact]
    public async Task ADueJobRunsAndKeepsRunning()
    {
        var job = new CountingJob { RunsBeforeSignal = 2 };
        JobHost host = Host(job, new GrantingLock());

        await host.StartAsync(CancellationToken.None);
        await job.Reached.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.True(job.Runs >= 2);
    }

    [Fact]
    public async Task AHeldLockSkipsTheTickWithoutRunningTheJob()
    {
        var job = new CountingJob();
        var denying = new DenyingLock();
        JobHost host = Host(job, denying);

        await host.StartAsync(CancellationToken.None);
        await denying.Attempted.WaitAsync(Timeout); // two ticks tried the lock...
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(0, job.Runs); // ...and the job never ran
    }

    [Fact]
    public async Task AFailingRunDoesNotKillTheLoop()
    {
        var job = new CountingJob { FailFirstRun = true, RunsBeforeSignal = 3 };
        JobHost host = Host(job, new GrantingLock());

        await host.StartAsync(CancellationToken.None);
        await job.Reached.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.True(job.Runs >= 3); // runs 2 and 3 happened after run 1 threw
    }

    private sealed class UnconstructibleJob : IScheduledJob
    {
        public UnconstructibleJob() =>
            throw new InvalidOperationException("Simulated missing dependency.");

        public string Name => "test.unconstructible";

        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMilliseconds(20));

        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task AJobThatCannotBeConstructed_DiesLoudlyWithoutTakingTheOthersDown()
    {
        // The composition defect is logged and that loop ends; every OTHER job keeps running —
        // previously the fault sat unobserved in Task.WhenAll until shutdown, silently.
        var healthy = new CountingJob { RunsBeforeSignal = 2 };

        var services = new ServiceCollection();
        services.AddSingleton(healthy);
        services.AddSingleton<IJobLock>(new GrantingLock());
        services.AddScheduledJob<CountingJob>();
        services.AddScheduledJob<UnconstructibleJob>();
        services.AddSingleton<IClock>(new FakeClock(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        ServiceProvider provider = services.BuildServiceProvider();

        var host = new JobHost(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IClock>(),
            Options.Create(new JobHostOptions()),
            NullLogger<JobHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await healthy.Reached.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.True(healthy.Runs >= 2);
    }

    [Fact]
    public void RegisteringTheSameJobTwice_YieldsOneLoop()
    {
        // Module extensions are idempotent by convention (TryAdd everywhere); a double call must
        // not be the thing that quietly doubles a sweep's frequency.
        var services = new ServiceCollection();
        services.AddScheduledJob<CountingJob>();
        services.AddScheduledJob<CountingJob>();

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Single(provider.GetServices<ScheduledJobRegistration>());
    }

    [Fact]
    public async Task ADisabledHostRunsNothing()
    {
        var job = new CountingJob();
        JobHost host = Host(job, new GrantingLock(), enabled: false);

        await host.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(0, job.Runs);
    }
}
