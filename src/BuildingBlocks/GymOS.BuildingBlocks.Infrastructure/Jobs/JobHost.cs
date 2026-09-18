using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// The job runner (M3.4): one background loop per registered <see cref="IScheduledJob"/>.
///
/// <para>Each tick: wait for the schedule's next due time, open a fresh DI scope, take the
/// cross-instance <see cref="IJobLock"/> (skip the tick when another instance holds it), resolve
/// the job in that scope, run it with the clock read at the edge. <b>A failing run never kills
/// the loop</b> — the error is logged with the job's name and the schedule advances, because the
/// sweeps derive their work from state and the next tick simply finds it still there.</para>
///
/// <para>Runs inside the API host today; when a dedicated worker entrypoint appears (the compose
/// file anticipates one sharing this image), moving the jobs is a host-composition change, not a
/// rewrite — nothing here knows what host it is in.</para>
/// </summary>
public sealed partial class JobHost : BackgroundService
{
    private readonly List<ScheduledJobRegistration> _registrations;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClock _clock;
    private readonly IOptions<JobHostOptions> _options;
    private readonly ILogger<JobHost> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Job host started: {JobCount} job(s) registered.")]
    private static partial void LogStarted(ILogger logger, int jobCount);

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Job host disabled by configuration (Jobs:Enabled=false); no jobs will run in this process.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message =
        "Job {JobName} skipped this tick: another instance holds its lock.")]
    private static partial void LogSkippedLocked(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Job {JobName} finished in {ElapsedMs} ms.")]
    private static partial void LogFinished(ILogger logger, string jobName, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Job {JobName} failed; its schedule continues and the next tick retries whatever is " +
        "still due — the sweeps derive work from state, so nothing is lost with the tick.")]
    private static partial void LogFailed(ILogger logger, string jobName, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Job {JobType} could not be initialized and will NOT run in this process. This is a " +
        "composition defect (a missing registration, a throwing constructor) — fix it and " +
        "redeploy; until then this job's work simply does not happen.")]
    private static partial void LogLoopDead(ILogger logger, string jobType, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Job {JobName}: its run could not be recorded ({Stage}); the job still runs, this run " +
        "will simply be missing from the monitoring history.")]
    private static partial void LogRecordingFailed(ILogger logger, string jobName, string stage, Exception exception);

    public JobHost(
        IEnumerable<ScheduledJobRegistration> registrations,
        IServiceScopeFactory scopeFactory,
        IClock clock,
        IOptions<JobHostOptions> options,
        ILogger<JobHost> logger)
    {
        _registrations = registrations.ToList();
        _scopeFactory = scopeFactory;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            // Off for integration tests that drive the sweeps by hand, and for deployments that
            // want jobs on one replica profile only.
            LogDisabled(_logger);
            return;
        }

        LogStarted(_logger, _registrations.Count);

        await Task.WhenAll(_registrations.Select(registration => RunLoopAsync(registration, stoppingToken)));
    }

    private async Task RunLoopAsync(ScheduledJobRegistration registration, CancellationToken stoppingToken)
    {
        // Name and schedule are immutable configuration on the job type; read them once from a
        // throwaway instance so the loop does not need a live scope between ticks. Guarded: a
        // throwing constructor here would otherwise fault this loop's task, and Task.WhenAll only
        // observes that at shutdown — a job that silently never runs, with the host reading green.
        string name;
        JobSchedule schedule;
        try
        {
            using IServiceScope startupScope = _scopeFactory.CreateScope();
            var prototype = (IScheduledJob)startupScope.ServiceProvider.GetRequiredService(registration.JobType);
            name = prototype.Name;
            schedule = prototype.Schedule;
        }
        catch (Exception exception)
        {
            LogLoopDead(_logger, registration.JobType.Name, exception);
            return;
        }

        // The cadence anchors to the SCHEDULE, not to when runs finish: each next tick is
        // computed from the previous tick's due time, so a slow run does not push every later
        // run out by its own duration. When the loop falls behind by more than a whole period
        // (host asleep, very long run), it re-baselines from the clock instead of firing a
        // catch-up storm — the sweeps derive work from state, so missed ticks carry nothing.
        DateTimeOffset next = schedule.NextAfter(_clock.UtcNow);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay = next - _clock.UtcNow;

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }

                await RunOnceAsync(registration.JobType, name, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return; // Host shutdown — not a failure.
            }
            catch (Exception exception)
            {
                LogFailed(_logger, name, exception);
            }

            next = schedule.NextAfter(next);
            if (next <= _clock.UtcNow)
            {
                next = schedule.NextAfter(_clock.UtcNow);
            }
        }
    }

    private async Task RunOnceAsync(Type jobType, string name, CancellationToken stoppingToken)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();

        IJobLock jobLock = scope.ServiceProvider.GetRequiredService<IJobLock>();
        await using IAsyncDisposable? handle = await jobLock.TryAcquireAsync(name, stoppingToken);
        if (handle is null)
        {
            LogSkippedLocked(_logger, name);
            return;
        }

        // The recorder gets its own scope — its own DbContext and connection — so a job's failed
        // transaction cannot roll back its own failure row, and a job's scope disposing mid-Fail
        // cannot take the recorder with it (M6 spec §2).
        using IServiceScope recordingScope = _scopeFactory.CreateScope();
        IJobRunRecorder recorder = recordingScope.ServiceProvider.GetRequiredService<IJobRunRecorder>();

        DateTimeOffset startedAt = _clock.UtcNow;
        Guid? runId = await TryStartRecordingAsync(recorder, name, startedAt, stoppingToken);

        long startedAtTicks = Environment.TickCount64;
        try
        {
            // Resolved AFTER Start so a constructor that throws is still a recorded failure.
            var job = (IScheduledJob)scope.ServiceProvider.GetRequiredService(jobType);
            await job.ExecuteAsync(startedAt, stoppingToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && stoppingToken.IsCancellationRequested))
        {
            await TryFinishRecordingAsync(
                recorder, name, runId, r => recorder.FailAsync(r, _clock.UtcNow, JobRunError.Describe(exception), CancellationToken.None));
            throw;
        }

        LogFinished(_logger, name, Environment.TickCount64 - startedAtTicks);
        await TryFinishRecordingAsync(recorder, name, runId, r => recorder.SucceedAsync(r, _clock.UtcNow, stoppingToken));
    }

    private async Task<Guid?> TryStartRecordingAsync(
        IJobRunRecorder recorder, string name, DateTimeOffset startedAt, CancellationToken stoppingToken)
    {
        try
        {
            return await recorder.StartAsync(name, Environment.MachineName, startedAt, stoppingToken);
        }
        catch (Exception exception) when (!(exception is OperationCanceledException && stoppingToken.IsCancellationRequested))
        {
            LogRecordingFailed(_logger, name, "start", exception);
            return null;
        }
    }

    private async Task TryFinishRecordingAsync(IJobRunRecorder recorder, string name, Guid? runId, Func<Guid, Task> finish)
    {
        if (runId is not Guid id)
        {
            return; // Start never landed; there is no row to finish.
        }

        try
        {
            await finish(id);
        }
        catch (Exception exception)
        {
            LogRecordingFailed(_logger, name, "finish", exception);
        }
    }
}

/// <summary>
/// Marks a job type for the host. A registration record rather than registering
/// <see cref="IScheduledJob"/> directly, because jobs are scoped (they use scoped services like
/// the DbContext) while the host needs the full list at startup without holding a scope open.
/// </summary>
public sealed record ScheduledJobRegistration(Type JobType);

/// <summary>Bound from the <c>Jobs</c> configuration section.</summary>
public sealed class JobHostOptions
{
    public const string SectionName = "Jobs";

    /// <summary>On by default: a clean clone runs its jobs. Off for hand-driven tests.</summary>
    public bool Enabled { get; set; } = true;
}
