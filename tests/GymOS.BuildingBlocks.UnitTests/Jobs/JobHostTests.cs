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

    private sealed class RecordingRecorder : IJobRunRecorder
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public string? LastError { get; private set; }

        public bool ThrowOnStart { get; init; }

        public CancellationToken? SucceedToken { get; private set; }

        public Task Finished => _finished.Task;

        public Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
        {
            if (ThrowOnStart)
            {
                throw new InvalidOperationException("recorder unavailable");
            }

            lock (Calls) { Calls.Add($"start:{jobName}"); }
            return Task.FromResult(Guid.NewGuid());
        }

        public Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken)
        {
            SucceedToken = cancellationToken;
            lock (Calls) { Calls.Add("succeed"); }
            _finished.TrySetResult();
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken)
        {
            lock (Calls) { Calls.Add("fail"); }
            LastError = error;
            _finished.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private static JobHost Host(CountingJob job, IJobLock jobLock, bool enabled = true, IJobRunRecorder? recorder = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(job);
        services.AddSingleton(jobLock);
        services.AddScheduledJob<CountingJob>(); // TryAddScoped defers to the singleton above
        services.AddSingleton<IClock>(new FakeClock(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<IJobRunRecorder>(recorder ?? new RecordingRecorder());

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

    private sealed class GateControlledJob : IScheduledJob
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once <see cref="ExecuteAsync"/> has been entered — the run is in progress.</summary>
        public Task Started => _started.Task;

        public string Name => "test.gated";

        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMilliseconds(20));

        public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task; // held open until the test releases it, regardless of cancellationToken
        }

        /// <summary>Lets the in-flight <see cref="ExecuteAsync"/> return, as if the job just succeeded.</summary>
        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// Simulates what a real recorder's DB call would do if handed an already-cancelled token:
    /// throw. Used to prove <see cref="JobHost"/> finishes a successful run with
    /// <see cref="CancellationToken.None"/> rather than the host's stopping token, so a success
    /// that lands while the host is shutting down is still recorded.
    /// </summary>
    private sealed class ThrowsIfCancelledRecorder : IJobRunRecorder
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public Task Finished => _finished.Task;

        public Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
        {
            lock (Calls) { Calls.Add($"start:{jobName}"); }
            return Task.FromResult(Guid.NewGuid());
        }

        public Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Calls) { Calls.Add("succeed"); }
            _finished.TrySetResult();
            return Task.CompletedTask;
        }

        public Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken)
        {
            lock (Calls) { Calls.Add("fail"); }
            _finished.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ASuccessThatFinishesDuringShutdownIsStillRecorded()
    {
        // Regression for the final review's finding: SucceedAsync must be called with
        // CancellationToken.None, not the host's stopping token, so a run that succeeds while
        // the host is shutting down still gets its row written (a success at shutdown must not
        // look like a run that never finished).
        var job = new GateControlledJob();
        var recorder = new ThrowsIfCancelledRecorder();

        var services = new ServiceCollection();
        services.AddSingleton(job);
        services.AddSingleton<IJobLock>(new GrantingLock());
        services.AddScheduledJob<GateControlledJob>(); // TryAddScoped defers to the singleton above
        services.AddSingleton<IClock>(new FakeClock(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton<IJobRunRecorder>(recorder);

        ServiceProvider provider = services.BuildServiceProvider();

        var host = new JobHost(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IClock>(),
            Options.Create(new JobHostOptions()),
            NullLogger<JobHost>.Instance);

        await host.StartAsync(CancellationToken.None);
        await job.Started.WaitAsync(Timeout); // the job is now mid-run, blocked on the gate

        try
        {
            // Cancels the stopping token that flows into RunOnceAsync without waiting for the
            // (still-blocked) loop to actually stop — StopAsync's own wait throws because the
            // token it was given to bound that wait is already cancelled; that is expected here.
            await host.StopAsync(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException)
        {
        }

        job.Release(); // let the job's ExecuteAsync return, as if it succeeded just as the host stopped
        await recorder.Finished.WaitAsync(Timeout);

        Assert.Contains("succeed", recorder.Calls);
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
        services.AddSingleton<IJobRunRecorder>(new RecordingRecorder());
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

    [Fact]
    public async Task ASuccessfulRunIsRecordedAsStartThenSucceed()
    {
        var job = new CountingJob { RunsBeforeSignal = 1 };
        var recorder = new RecordingRecorder();
        JobHost host = Host(job, new GrantingLock(), recorder: recorder);

        await host.StartAsync(CancellationToken.None);
        await recorder.Finished.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal("start:test.counting", recorder.Calls[0]);
        Assert.Equal("succeed", recorder.Calls[1]);
    }

    [Fact]
    public async Task AFailingRunIsRecordedAsFailWithTheTrimmedError()
    {
        var job = new CountingJob { RunsBeforeSignal = 1, FailFirstRun = true };
        var recorder = new RecordingRecorder();
        JobHost host = Host(job, new GrantingLock(), recorder: recorder);

        await host.StartAsync(CancellationToken.None);
        await recorder.Finished.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal("fail", recorder.Calls[1]);
        Assert.Equal("InvalidOperationException: First run fails by design.", recorder.LastError);
    }

    [Fact]
    public async Task ASkippedTickRecordsNothing()
    {
        var job = new CountingJob();
        var recorder = new RecordingRecorder();
        var denying = new DenyingLock();
        JobHost host = Host(job, denying, recorder: recorder);

        await host.StartAsync(CancellationToken.None);
        await denying.Attempted.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.Empty(recorder.Calls);
    }

    [Fact]
    public async Task ARecorderThatCannotStartDoesNotStopTheJobRunning()
    {
        var job = new CountingJob { RunsBeforeSignal = 1 };
        JobHost host = Host(job, new GrantingLock(), recorder: new RecordingRecorder { ThrowOnStart = true });

        await host.StartAsync(CancellationToken.None);
        await job.Reached.WaitAsync(Timeout);
        await host.StopAsync(CancellationToken.None);

        Assert.True(job.Runs >= 1);
    }
}
