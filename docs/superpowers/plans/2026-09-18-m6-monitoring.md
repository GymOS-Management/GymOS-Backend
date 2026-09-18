# M6 — Monitoring Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every job run is recorded by the job host in `platform.job_runs`; `/api/v1/monitoring/*` serves an overview (health, job state, outbox depth, live process metrics) and per-job run history; `RetentionJob` sweeps runs older than 30 days keeping the newest per job.

**Architecture:** Recording lives in `JobHost` behind `IJobRunRecorder` (BuildingBlocks), implemented in Platform infrastructure over EF Core and SQL Server. Read models are live queries through `IMonitoringQueries` (Application abstraction, Infrastructure implementation), with health and process metrics adapted in the host project because their types are host-only. Endpoints follow `AnalyticsEndpoints`: versioned group, `RequireAuthorization`, queries dispatched through `IDispatcher` with `[RequiresPermission]`.

**Tech Stack:** .NET 10 (SDK 10.0.400 pinned via `global.json` `rollForward: latestFeature`), ASP.NET Core minimal APIs, EF Core with `UseSnakeCaseNamingConvention`, SQL Server (Testcontainers in tests), xUnit. No new packages.

**Spec:** `docs/superpowers/specs/2026-09-18-m6-monitoring-design.md`

## Global Constraints

- Build must be clean with warnings as errors: `dotnet build GymOS.Backend.slnx -c Release`.
- `dotnet format --verify-no-changes` must pass before every commit.
- Table lives in schema `platform`, table name `job_runs`, snake_case columns (the naming convention derives them — never set `HasColumnName` by hand).
- Error text stored per run: `exception.GetType().Name + ": " + exception.Message`, truncated to **2000** characters, never a stack trace.
- Retention default **30** days from `Monitoring:JobRunRetentionDays`; stale grace default **5 minutes** from `Monitoring:StaleGrace`; the newest run per job is always kept.
- Retry/transient codes and job names are never invented: job names are the `IScheduledJob.Name` strings that exist (`retention`, `renewal`, etc. — read them from the classes, do not guess).
- New permission key is exactly `monitoring.read`, granted to SuperAdmin (implicit), PlatformAdmin, SupportAgent and ReadOnly.
- Skipped ticks (lock held by another instance) are **not** recorded.
- Recorder failures never fail the job run.
- Migration and the declarative `database/GymOS.Database` transcription land **in the same commit** (DATABASE.md §3).
- OpenAPI snapshot is regenerated and its diff read when endpoints change (`UPDATE_OPENAPI_SNAPSHOT=1`).
- Commits go straight to `development` (the owner's standing instruction for this work); each task ends in a commit.
- Every Bash command in this plan runs from `C:\Work\Personal-Projects\GymOS\GymOS-Backend`. Docker Desktop must be running for any `GymOS.IntegrationTests` run.
- Git Bash: `export MSYS_NO_PATHCONV=1` before any `docker exec` with container paths.

---

## File map

**BuildingBlocks**
- Modify `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/JobSchedule.cs` — add `Period`, `Describe()`.
- Create `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/IJobRunRecorder.cs`.
- Create `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/IRegisteredJobs.cs` — `RegisteredJob` record + interface.
- Create `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobRunError.cs` — the trimming rule.
- Create `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/RegisteredJobsCatalog.cs`.
- Modify `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobHost.cs` — record runs.
- Modify `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobsServiceCollectionExtensions.cs` — register the catalog.

**Platform.Domain**
- Create `src/Platform/GymOS.Platform.Domain/Monitoring/JobRun.cs` — entity + `JobRunStatus`.
- Modify `src/Platform/GymOS.Platform.Domain/Identity/PlatformPermissions.cs`, `SystemRoles.cs`.

**Platform.Application**
- Create `src/Platform/GymOS.Platform.Application/Monitoring/MonitoringOptions.cs`.
- Create `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/MonitoringContracts.cs` — DTOs.
- Create `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/IMonitoringQueries.cs`.
- Create `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/IProcessMetrics.cs`.
- Create `src/Platform/GymOS.Platform.Application/Monitoring/JobStateAssembler.cs` — stale rule + composition.
- Create `src/Platform/GymOS.Platform.Application/Monitoring/MonitoringQueriesHandlers.cs` — three queries + handlers.

**Platform.Infrastructure**
- Create `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/JobRunConfiguration.cs`.
- Create migration `AddJobRuns` under `Persistence/Migrations/` (generated).
- Create `src/Platform/GymOS.Platform.Infrastructure/Monitoring/JobRunRecorder.cs`.
- Create `src/Platform/GymOS.Platform.Infrastructure/Monitoring/JobRunMaintainer.cs`.
- Create `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/MonitoringQueries.cs`.
- Create `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformMonitoringServiceCollectionExtensions.cs`.
- Modify `src/Platform/GymOS.Platform.Infrastructure/Jobs/RetentionJob.cs` — fifth step.

**Host**
- Create `src/Host/GymOS.WebApi/Monitoring/ProcessMetricsSampler.cs`.
- Create `src/Host/GymOS.WebApi/Endpoints/MonitoringEndpoints.cs`.
- Modify `src/Host/GymOS.WebApi/Program.cs` — `AddPlatformMonitoring`, sampler, `MapMonitoringEndpoints`.

**Database project**
- Create `database/GymOS.Database/Tables/platform/job_runs.sql`.

**Tests**
- Modify `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobScheduleTests.cs`, `JobHostTests.cs`.
- Create `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobRunErrorTests.cs`, `RegisteredJobsCatalogTests.cs`.
- Create `tests/GymOS.Platform.Application.UnitTests/JobStateAssemblerTests.cs`, `MonitoringQueryHandlerTests.cs`.
- Modify `tests/GymOS.Platform.Infrastructure.UnitTests/ConcurrencyModelTests.cs` — exemption.
- Create `tests/GymOS.IntegrationTests/JobRunRecordingTests.cs`, `JobRunMaintainerTests.cs`, `MonitoringApiTests.cs`.
- Modify `tests/GymOS.IntegrationTests/RetentionJobTests.cs` — new constructor argument.
- Regenerate the OpenAPI snapshot (`OpenApiContractTests`).

**Docs**
- Create `docs/BusinessRules/Monitoring.md`; modify `docs/RELEASE-NOTES.md`, `docs/STATUS.md`.

---

### Task 1: `JobSchedule.Period` and `Describe()`

**Files:**
- Modify: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/JobSchedule.cs`
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobScheduleTests.cs`

**Interfaces:**
- Produces: `public abstract TimeSpan Period { get; }` and `public abstract string Describe();` on `JobSchedule`. Interval: `Period == interval`, `Describe()` is `"every 15m"` / `"every 1h"` / `"every 1.5h"` style (see implementation). Daily: `Period == TimeSpan.FromDays(1)`, `Describe()` is `"daily at 03:10 UTC"`.

- [ ] **Step 1: Write the failing tests**

Append to `JobScheduleTests`:

```csharp
    [Fact]
    public void AnIntervalSchedulesPeriodIsItsInterval() =>
        Assert.Equal(TimeSpan.FromMinutes(15), JobSchedule.Every(TimeSpan.FromMinutes(15)).Period);

    [Fact]
    public void ADailySchedulesPeriodIsOneDay() =>
        Assert.Equal(TimeSpan.FromDays(1), JobSchedule.DailyAtUtc(new TimeOnly(3, 10)).Period);

    [Theory]
    [InlineData(15, "every 15m")]
    [InlineData(60, "every 1h")]
    [InlineData(90, "every 1h 30m")]
    public void AnIntervalScheduleDescribesItselfInMinutesAndHours(int minutes, string expected) =>
        Assert.Equal(expected, JobSchedule.Every(TimeSpan.FromMinutes(minutes)).Describe());

    [Fact]
    public void ASubMinuteIntervalDescribesItselfInSeconds() =>
        Assert.Equal("every 20s", JobSchedule.Every(TimeSpan.FromSeconds(20)).Describe());

    [Fact]
    public void ADailyScheduleDescribesItsUtcTime() =>
        Assert.Equal("daily at 03:10 UTC", JobSchedule.DailyAtUtc(new TimeOnly(3, 10)).Describe());
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~JobScheduleTests"`
Expected: build error — `Period` and `Describe` do not exist.

- [ ] **Step 3: Implement**

In `JobSchedule`, add after `NextAfter`:

```csharp
    /// <summary>
    /// How often the job is meant to run: the interval, or one day for a daily schedule. The
    /// monitoring read side measures staleness in multiples of this.
    /// </summary>
    public abstract TimeSpan Period { get; }

    /// <summary>A short human description for a console row: "every 15m", "daily at 03:10 UTC".</summary>
    public abstract string Describe();
```

In `IntervalSchedule`:

```csharp
        public override TimeSpan Period => Interval;

        public override string Describe()
        {
            if (Interval < TimeSpan.FromMinutes(1))
            {
                return $"every {(int)Interval.TotalSeconds}s";
            }

            int hours = (int)Interval.TotalHours;
            int minutes = Interval.Minutes;

            return (hours, minutes) switch
            {
                (0, _) => $"every {minutes}m",
                (_, 0) => $"every {hours}h",
                _ => $"every {hours}h {minutes}m",
            };
        }
```

In `DailySchedule`:

```csharp
        public override TimeSpan Period => TimeSpan.FromDays(1);

        public override string Describe() => $"daily at {TimeOfDayUtc:HH\\:mm} UTC";
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~JobScheduleTests"`
Expected: all PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/JobSchedule.cs tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobScheduleTests.cs
git commit -m "jobs: JobSchedule exposes its period and a description for the monitoring page

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 2: `IJobRunRecorder`, `JobRunError`, and the host records every run

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/IJobRunRecorder.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobRunError.cs`
- Modify: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobHost.cs`
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobRunErrorTests.cs`, `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobHostTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public interface IJobRunRecorder
  {
      Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken);
      Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken);
      Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken);
  }
  public static class JobRunError { public const int MaxLength = 2000; public static string Describe(Exception exception); }
  ```
- The host resolves `IJobRunRecorder` from a **separate** DI scope per run (`GetRequiredService`). Composition roots must register one (Task 5 does for the API; tests register a fake).

- [ ] **Step 1: Write the failing tests for the trimming rule**

Create `tests/GymOS.BuildingBlocks.UnitTests/Jobs/JobRunErrorTests.cs`:

```csharp
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

public sealed class JobRunErrorTests
{
    [Fact]
    public void ItIsTheTypeNameAndMessageOnly()
    {
        var exception = new InvalidOperationException("renewal failed for gym 7");

        Assert.Equal("InvalidOperationException: renewal failed for gym 7", JobRunError.Describe(exception));
    }

    [Fact]
    public void ItNeverCarriesAStackTraceOrInnerException()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException("outer", new ArgumentException("inner detail with Server=secret"));
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        string described = JobRunError.Describe(thrown);

        Assert.DoesNotContain("   at ", described, StringComparison.Ordinal);
        Assert.DoesNotContain("inner detail", described, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsTruncatedToTheColumnWidth()
    {
        var exception = new InvalidOperationException(new string('x', 5000));

        Assert.Equal(JobRunError.MaxLength, JobRunError.Describe(exception).Length);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~JobRunErrorTests"`
Expected: build error — `JobRunError` does not exist.

- [ ] **Step 3: Create the interface and the trimming rule**

`src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/IJobRunRecorder.cs`:

```csharp
namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>
/// Writes the history of job runs (M6). Called by the job host around every run it performs — a
/// tick skipped because another instance held the lock is not a run and is not recorded.
///
/// <para>The host calls this on its own DI scope, separate from the job's, so a job's failed
/// transaction never rolls back its own failure row, and the host treats a recorder failure as a
/// warning, never as a reason not to run the job. Monitoring must never be why billing did not
/// renew.</para>
/// </summary>
public interface IJobRunRecorder
{
    /// <summary>Writes the Running row and returns its id for the matching finish call.</summary>
    Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken);

    Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken);

    /// <summary><paramref name="error"/> is already trimmed by the host: type and message, no stack.</summary>
    Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken);
}
```

`src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobRunError.cs`:

```csharp
namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// The one shape of error text a job run may store: the exception's type name and message, cut
/// to the column width. Never the stack trace and never the inner chain — a <c>SqlException</c>
/// message can name a server, and <c>job_runs.error</c> is read by a console panel with
/// <c>monitoring.read</c>, not by a debugger. The full exception is in the structured log under
/// the same job name and time.
/// </summary>
public static class JobRunError
{
    public const int MaxLength = 2000;

    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        string text = $"{exception.GetType().Name}: {exception.Message}";

        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
```

- [ ] **Step 4: Run the error tests to verify they pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~JobRunErrorTests"`
Expected: 3 PASS.

- [ ] **Step 5: Write the failing host tests**

In `JobHostTests.cs`, add a recording fake and update the `Host(...)` helper so every existing test compiles (it now needs a recorder registered):

```csharp
    private sealed class RecordingRecorder : IJobRunRecorder
    {
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public string? LastError { get; private set; }

        public bool ThrowOnStart { get; init; }

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
```

Change the helper signature to `private static JobHost Host(CountingJob job, IJobLock jobLock, bool enabled = true, IJobRunRecorder? recorder = null)` and add `services.AddSingleton<IJobRunRecorder>(recorder ?? new RecordingRecorder());` after the lock registration. (The `AJobThatCannotBeConstructed_DiesLoudlyWithoutTakingTheOthersDown` test builds its own provider around line 170 — add the same `AddSingleton<IJobRunRecorder>(new RecordingRecorder())` line there.)

Add the tests:

```csharp
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
```

- [ ] **Step 6: Run to verify the new host tests fail**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~JobHostTests"`
Expected: the four new tests FAIL (recorder never called / empty calls list) — existing tests still PASS.

- [ ] **Step 7: Record runs in `JobHost.RunOnceAsync`**

Add two log messages beside the existing ones:

```csharp
    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Job {JobName}: its run could not be recorded ({Stage}); the job still runs, this run " +
        "will simply be missing from the monitoring history.")]
    private static partial void LogRecordingFailed(ILogger logger, string jobName, string stage, Exception exception);
```

Replace `RunOnceAsync` with:

```csharp
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
```

Note: `ExecuteAsync(startedAt, …)` passes the same `now` the row records, which is the AD-40 rule already followed (`_clock.UtcNow` read once at the edge). The `FailAsync` call uses `CancellationToken.None` deliberately: a failure during shutdown should still be written if the database is reachable.

- [ ] **Step 8: Run all BuildingBlocks unit tests**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests`
Expected: all PASS, including the four new host tests and the existing five.

- [ ] **Step 9: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/BuildingBlocks tests/GymOS.BuildingBlocks.UnitTests
git commit -m "jobs: the host records every run through IJobRunRecorder

Start before the job is resolved, Succeed or Fail after; a skipped tick records
nothing; a recorder failure is a warning, never a reason the job did not run.
Error text is type and message, trimmed to 2000 characters, never a stack.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 3: `IRegisteredJobs` — the registered set, with names and schedules

**Files:**
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Application/Jobs/IRegisteredJobs.cs`
- Create: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/RegisteredJobsCatalog.cs`
- Modify: `src/BuildingBlocks/GymOS.BuildingBlocks.Infrastructure/Jobs/JobsServiceCollectionExtensions.cs`
- Test: `tests/GymOS.BuildingBlocks.UnitTests/Jobs/RegisteredJobsCatalogTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record RegisteredJob(string Name, JobSchedule Schedule);
  public interface IRegisteredJobs { IReadOnlyList<RegisteredJob> All { get; } }
  ```
  Registered as a singleton by `AddJobHost()`. Resolves each job's prototype once, lazily, in a throwaway scope (the same way `JobHost.RunLoopAsync` does); a job whose constructor throws is omitted and logged.

- [ ] **Step 1: Write the failing test**

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

public sealed class RegisteredJobsCatalogTests
{
    private sealed class AlphaJob : IScheduledJob
    {
        public string Name => "alpha";
        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMinutes(15));
        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BrokenJob : IScheduledJob
    {
        public BrokenJob() => throw new InvalidOperationException("no dependencies for me");
        public string Name => "broken";
        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMinutes(1));
        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void ItListsEveryRegisteredJobsNameAndSchedule()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<AlphaJob>();
        services.AddSingleton<IRegisteredJobs>(provider => new RegisteredJobsCatalog(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegisteredJobsCatalog>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();

        RegisteredJob job = Assert.Single(provider.GetRequiredService<IRegisteredJobs>().All);

        Assert.Equal("alpha", job.Name);
        Assert.Equal(TimeSpan.FromMinutes(15), job.Schedule.Period);
    }

    [Fact]
    public void AJobThatCannotBeConstructedIsOmittedNotFatal()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<AlphaJob>();
        services.AddScheduledJob<BrokenJob>();
        services.AddSingleton<IRegisteredJobs>(provider => new RegisteredJobsCatalog(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegisteredJobsCatalog>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(["alpha"], provider.GetRequiredService<IRegisteredJobs>().All.Select(job => job.Name));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests --filter "FullyQualifiedName~RegisteredJobsCatalogTests"`
Expected: build error — types missing.

- [ ] **Step 3: Implement**

`IRegisteredJobs.cs`:

```csharp
namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>One job the host knows about: its stable name and when it runs.</summary>
public sealed record RegisteredJob(string Name, JobSchedule Schedule);

/// <summary>
/// The set of jobs registered with the host, for the monitoring page (M6): a job that has never
/// run still has a row there, because the registration exists even when the history does not.
/// </summary>
public interface IRegisteredJobs
{
    IReadOnlyList<RegisteredJob> All { get; }
}
```

`RegisteredJobsCatalog.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// Reads each registered job's name and schedule once, from a throwaway prototype in its own
/// scope — exactly what <see cref="JobHost.RunLoopAsync"/> does at start-up, for the same reason
/// (jobs are scoped; the catalog is a singleton). A job whose constructor throws is left out and
/// logged: the host already logs that job as dead, and a monitoring page that could not load
/// would hide every other job behind it.
/// </summary>
public sealed partial class RegisteredJobsCatalog : IRegisteredJobs
{
    private readonly Lazy<IReadOnlyList<RegisteredJob>> _all;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Job {JobType} could not be described for monitoring and is omitted from the registered set.")]
    private static partial void LogOmitted(ILogger logger, string jobType, Exception exception);

    public RegisteredJobsCatalog(
        IEnumerable<ScheduledJobRegistration> registrations,
        IServiceScopeFactory scopeFactory,
        ILogger<RegisteredJobsCatalog> logger)
    {
        List<ScheduledJobRegistration> list = registrations.ToList();
        _all = new Lazy<IReadOnlyList<RegisteredJob>>(() => Describe(list, scopeFactory, logger));
    }

    public IReadOnlyList<RegisteredJob> All => _all.Value;

    private static IReadOnlyList<RegisteredJob> Describe(
        List<ScheduledJobRegistration> registrations, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        var jobs = new List<RegisteredJob>(registrations.Count);
        using IServiceScope scope = scopeFactory.CreateScope();

        foreach (ScheduledJobRegistration registration in registrations)
        {
            try
            {
                var prototype = (IScheduledJob)scope.ServiceProvider.GetRequiredService(registration.JobType);
                jobs.Add(new RegisteredJob(prototype.Name, prototype.Schedule));
            }
            catch (Exception exception)
            {
                LogOmitted(logger, registration.JobType.Name, exception);
            }
        }

        return jobs.OrderBy(job => job.Name, StringComparer.Ordinal).ToList();
    }
}
```

In `JobsServiceCollectionExtensions.AddJobHost`, after `AddHostedService<JobHost>()`:

```csharp
        services.TryAddSingleton<IRegisteredJobs, RegisteredJobsCatalog>();
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/GymOS.BuildingBlocks.UnitTests`
Expected: all PASS.

- [ ] **Step 5: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/BuildingBlocks tests/GymOS.BuildingBlocks.UnitTests/Jobs/RegisteredJobsCatalogTests.cs
git commit -m "jobs: IRegisteredJobs lists every registered job's name and schedule

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 4: `JobRun` entity, configuration, migration, declarative transcription

**Files:**
- Create: `src/Platform/GymOS.Platform.Domain/Monitoring/JobRun.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Configurations/JobRunConfiguration.cs`
- Create (generated): `src/Platform/GymOS.Platform.Infrastructure/Persistence/Migrations/<timestamp>_AddJobRuns.cs` + Designer + snapshot update
- Create: `database/GymOS.Database/Tables/platform/job_runs.sql`
- Modify: `tests/GymOS.Platform.Infrastructure.UnitTests/ConcurrencyModelTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace GymOS.Platform.Domain.Monitoring;
  public enum JobRunStatus { Running = 1, Succeeded = 2, Failed = 3 }
  public sealed class JobRun : Entity
  {
      public const int MaxNameLength = 100, MaxInstanceLength = 100, MaxErrorLength = 2000;
      public static JobRun Start(Guid id, string jobName, string instance, DateTimeOffset startedAtUtc);
      public string JobName { get; } public string Instance { get; } public JobRunStatus Status { get; }
      public DateTimeOffset StartedAtUtc { get; } public DateTimeOffset? FinishedAtUtc { get; }
      public long? DurationMs { get; } public string? Error { get; }
  }
  ```
  Finishing a run is done by the recorder with `ExecuteUpdateAsync` (Task 5), never by loading and saving, so the entity has no mutators.

- [ ] **Step 1: Write the entity**

```csharp
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Monitoring;

/// <summary>One run's outcome. <c>Running</c> is the truth for a process that died mid-run; nothing rewrites it.</summary>
public enum JobRunStatus
{
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

/// <summary>
/// One execution of one <c>IScheduledJob</c>, written by the job host (M6 spec §2–3). A plain entity
/// rather than an aggregate: it has one legal path, <c>Running → Succeeded | Failed</c>, that only the
/// host walks, and the finish is a single <c>UPDATE … WHERE id = @id</c> the recorder issues without
/// loading the row. No concurrency token for the same reason — one writer per row, serialised by
/// <c>IJobLock</c>, and a finished row is never touched again.
/// </summary>
public sealed class JobRun : Entity
{
    public const int MaxNameLength = 100;
    public const int MaxInstanceLength = 100;
    public const int MaxErrorLength = 2000;

    private JobRun(Guid id, string jobName, string instance, DateTimeOffset startedAtUtc)
        : base(id)
    {
        JobName = jobName;
        Instance = instance;
        StartedAtUtc = startedAtUtc;
        Status = JobRunStatus.Running;
    }

    private JobRun()
    {
    }

    public string JobName { get; private set; } = string.Empty;

    /// <summary>The replica or container that ran it (<c>Environment.MachineName</c>).</summary>
    public string Instance { get; private set; } = string.Empty;

    public JobRunStatus Status { get; private set; }

    public DateTimeOffset StartedAtUtc { get; private set; }

    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public long? DurationMs { get; private set; }

    /// <summary>Type name and message only, at most <see cref="MaxErrorLength"/> characters; null unless Failed.</summary>
    public string? Error { get; private set; }

    public static JobRun Start(Guid id, string jobName, string instance, DateTimeOffset startedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance);

        return new JobRun(
            id,
            jobName.Length <= MaxNameLength ? jobName : jobName[..MaxNameLength],
            instance.Length <= MaxInstanceLength ? instance : instance[..MaxInstanceLength],
            startedAtUtc);
    }
}
```

- [ ] **Step 2: Write the configuration**

```csharp
using GymOS.Platform.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GymOS.Platform.Infrastructure.Persistence.Configurations;

public sealed class JobRunConfiguration : IEntityTypeConfiguration<JobRun>
{
    public void Configure(EntityTypeBuilder<JobRun> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("job_runs", "platform");
        builder.HasKey(run => run.Id);

        builder.Property(run => run.JobName).HasMaxLength(JobRun.MaxNameLength).IsRequired();
        builder.Property(run => run.Instance).HasMaxLength(JobRun.MaxInstanceLength).IsRequired();
        builder.Property(run => run.Status).HasConversion<byte>();
        builder.Property(run => run.Error).HasMaxLength(JobRun.MaxErrorLength);

        // One index, three readers: the history page (newest first for one job), the newest-run-per-job
        // query behind the overview, and the retention sweep's "everything but the newest".
        builder.HasIndex(run => new { run.JobName, run.StartedAtUtc })
            .IsDescending(false, true)
            .HasDatabaseName("ix_job_runs_name_started");

        // No row version: one writer per row, serialised by IJobLock, and a finished row is never updated.
    }
}
```

- [ ] **Step 3: Add the concurrency exemption**

In `ConcurrencyModelTests.ExemptFromConcurrency`, add (with `using GymOS.Platform.Domain.Monitoring;`):

```csharp
        // One writer per row — the job host, serialised by IJobLock — and the only update is the
        // recorder's single `UPDATE … WHERE id = @id` from Running to a final status, issued without
        // loading the row. Nothing ever updates a finished run.
        [typeof(JobRun)] = "written by one host under IJobLock; finished by one condition-free UPDATE by id, never through SaveChanges",
```

- [ ] **Step 4: Build and run the infrastructure unit tests**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test tests/GymOS.Platform.Infrastructure.UnitTests`
Expected: build clean; `ConcurrencyModelTests` PASS (it discovers `JobRun` through the model and finds the exemption).

- [ ] **Step 5: Generate the migration**

Docker Desktop must be running with the dev stack's SQL Server up (`docker compose up -d sqlserver` — check the service name in `docker-compose.yml` first). Then:

```bash
dotnet ef migrations add AddJobRuns --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi
```

Open the generated `<timestamp>_AddJobRuns.cs` and confirm it contains exactly: `CreateTable("job_runs", schema: "platform", …)` with columns `id`, `job_name nvarchar(100)`, `instance nvarchar(100)`, `status tinyint`, `started_at_utc datetimeoffset`, `finished_at_utc datetimeoffset null`, `duration_ms bigint null`, `error nvarchar(2000) null`, and `CreateIndex("ix_job_runs_name_started", … descending: [false, true])`. Nothing else. If it contains anything else, the model changed by accident — stop and find out why.

Then: `dotnet ef migrations has-pending-model-changes --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi` → Expected: "No changes have been made to the model since the last migration."

- [ ] **Step 6: Transcribe into the declarative project**

Generate the script to read from: `dotnet ef migrations script RuntimeDatabaseProgrammability AddJobRuns --project src/Platform/GymOS.Platform.Infrastructure --startup-project src/Host/GymOS.WebApi --no-build`. Create `database/GymOS.Database/Tables/platform/job_runs.sql` matching it object by object, in the style of `daily_platform_stats.sql`:

```sql
/*
    [platform].[job_runs]

    Generated from the EF Core migrations, which remain the only mechanism that changes a
    database. This file is the declarative record of the result, used for schema comparison
    and drift detection only. See database/README.md.
*/

CREATE TABLE [platform].[job_runs] (

    [id] uniqueidentifier NOT NULL,
    [job_name] nvarchar(100) NOT NULL,
    [instance] nvarchar(100) NOT NULL,
    [status] tinyint NOT NULL,
    [started_at_utc] datetimeoffset NOT NULL,
    [finished_at_utc] datetimeoffset NULL,
    [duration_ms] bigint NULL,
    [error] nvarchar(2000) NULL,
    CONSTRAINT [pk_job_runs] PRIMARY KEY ([id])
);


GO

CREATE INDEX [ix_job_runs_name_started]
    ON [platform].[job_runs] ([job_name], [started_at_utc] DESC);
```

Check the primary key constraint name against the generated script (the naming convention produces `pk_job_runs`; if the script says otherwise, the script wins). Build the project: `dotnet build database/GymOS.Database/GymOS.Database.sqlproj` → Expected: succeeded.

- [ ] **Step 7: Run the integration suite's migration smoke**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~ApiEndpointTests"`
Expected: PASS (the fixture applies all migrations including `AddJobRuns` before `/health/ready` answers).

- [ ] **Step 8: Format and commit — migration and transcription together**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Domain/Monitoring src/Platform/GymOS.Platform.Infrastructure/Persistence database/GymOS.Database/Tables/platform/job_runs.sql tests/GymOS.Platform.Infrastructure.UnitTests/ConcurrencyModelTests.cs
git commit -m "monitoring: platform.job_runs — the table the job host writes

Entity, configuration, AddJobRuns migration and its declarative transcription in
one commit (DATABASE.md §3). One index serves the page, the overview and the sweep.

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 5: `JobRunRecorder`, `MonitoringOptions`, `AddPlatformMonitoring`, and the end-to-end recording test

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/MonitoringOptions.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/Monitoring/JobRunRecorder.cs`
- Create: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformMonitoringServiceCollectionExtensions.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs:86-96`
- Test: `tests/GymOS.IntegrationTests/JobRunRecordingTests.cs`

**Interfaces:**
- Consumes: `IJobRunRecorder` (Task 2), `JobRun`/`JobRunStatus` (Task 4).
- Produces:
  ```csharp
  namespace GymOS.Platform.Application.Monitoring;
  public sealed class MonitoringOptions { public const string SectionName = "Monitoring"; public int JobRunRetentionDays { get; set; } = 30; public TimeSpan StaleGrace { get; set; } = TimeSpan.FromMinutes(5); }
  // Infrastructure
  public static IServiceCollection AddPlatformMonitoring(this IServiceCollection services, IConfiguration configuration);
  internal sealed class JobRunRecorder : IJobRunRecorder  // AppDbContext-backed
  ```
  Later tasks add registrations to `AddPlatformMonitoring` (queries, maintainer).

- [ ] **Step 1: Write the failing integration test**

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.BuildingBlocks.Application.Abstractions;
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

        DateTimeOffset deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~JobRunRecordingTests"`
Expected: build error — `JobRunRecorder` does not exist.

- [ ] **Step 3: Create `MonitoringOptions`**

```csharp
using System.ComponentModel.DataAnnotations;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>
/// Bound from the <c>Monitoring</c> section. The retention window becomes a <c>platform_settings</c>
/// row when M7 lands; this option is its fallback and shares its name so the key can be the same.
/// </summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Job runs older than this are swept nightly, except the newest run of each job.</summary>
    [Range(1, 3650)]
    public int JobRunRetentionDays { get; set; } = 30;

    /// <summary>
    /// Added to a job's period before a late run reads as stale, so a merely slow run does not flicker red.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan StaleGrace { get; set; } = TimeSpan.FromMinutes(5);
}
```

- [ ] **Step 4: Create `JobRunRecorder`**

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Monitoring;

/// <summary>
/// Writes <c>platform.job_runs</c> for the job host (M6 spec §2). Start is an insert; the two finishes
/// are one <c>UPDATE … WHERE id = @id</c> each, issued without loading the row — nothing is tracked,
/// nothing is versioned, and a finished row is never touched again. Runs on the host's dedicated
/// recording scope, so this context is never the job's.
/// </summary>
internal sealed class JobRunRecorder : IJobRunRecorder
{
    private readonly AppDbContext _dbContext;

    public JobRunRecorder(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<Guid> StartAsync(
        string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        JobRun run = JobRun.Start(Guid.NewGuid(), jobName, instance, startedAtUtc);
        _dbContext.Set<JobRun>().Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.Entry(run).State = EntityState.Detached;

        return run.Id;
    }

    public Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken) =>
        FinishAsync(runId, JobRunStatus.Succeeded, finishedAtUtc, error: null, cancellationToken);

    public Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken) =>
        FinishAsync(
            runId,
            JobRunStatus.Failed,
            finishedAtUtc,
            error.Length <= JobRun.MaxErrorLength ? error : error[..JobRun.MaxErrorLength],
            cancellationToken);

    private async Task FinishAsync(
        Guid runId, JobRunStatus status, DateTimeOffset finishedAtUtc, string? error, CancellationToken cancellationToken)
    {
        // duration_ms is computed in SQL from the row's own started_at_utc, so the two columns can
        // never disagree, whatever clock the caller read.
        await _dbContext.Set<JobRun>()
            .Where(run => run.Id == runId && run.Status == JobRunStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Status, status)
                    .SetProperty(run => run.FinishedAtUtc, finishedAtUtc)
                    .SetProperty(run => run.DurationMs, run => (long)EF.Functions.DateDiffMillisecond(run.StartedAtUtc, finishedAtUtc))
                    .SetProperty(run => run.Error, error),
                cancellationToken);
    }
}
```

If `EF.Functions.DateDiffMillisecond` does not translate for `DateTimeOffset` on this EF version (compile error or runtime translation failure in Step 6), replace that setter with `.SetProperty(run => run.DurationMs, (long)(finishedAtUtc - startedAtUtc).TotalMilliseconds)` where `startedAtUtc` is passed down from the host — add it as a parameter to both finish methods on `IJobRunRecorder` **only if** the SQL route fails, and update Task 2's interface and fake accordingly. Prefer the SQL route.

- [ ] **Step 5: Create `AddPlatformMonitoring` and wire it in `Program.cs`**

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>Composes M6 monitoring: the run recorder the job host writes through, and its options.</summary>
public static class PlatformMonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<IJobRunRecorder, JobRunRecorder>();

        return services;
    }
}
```

In `Program.cs`, after `builder.Services.AddPlatformAnalytics();` (line 86):

```csharp
    // M6 monitoring: the job host's run recorder and the monitoring read side.
    builder.Services.AddPlatformMonitoring(builder.Configuration);
```

- [ ] **Step 6: Run the recording tests, then the host boot test**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~JobRunRecordingTests|FullyQualifiedName~ApiEndpointTests"`
Expected: all PASS. (`ApiEndpointTests` proves the real host boots with the recorder registered — without Step 5 the host would throw resolving `IJobRunRecorder` on the first tick.)

- [ ] **Step 7: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Application/Monitoring/MonitoringOptions.cs src/Platform/GymOS.Platform.Infrastructure/Monitoring/JobRunRecorder.cs src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformMonitoringServiceCollectionExtensions.cs src/Host/GymOS.WebApi/Program.cs tests/GymOS.IntegrationTests/JobRunRecordingTests.cs
git commit -m "monitoring: JobRunRecorder writes job_runs on the host's own scope

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 6: Retention — `JobRunMaintainer` and `RetentionJob`'s fifth step

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Monitoring/JobRunMaintainer.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/Jobs/RetentionJob.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformMonitoringServiceCollectionExtensions.cs`
- Modify: `tests/GymOS.IntegrationTests/RetentionJobTests.cs` (constructor)
- Test: `tests/GymOS.IntegrationTests/JobRunMaintainerTests.cs`

**Interfaces:**
- Consumes: `MonitoringOptions` (Task 5), `JobRun` (Task 4).
- Produces: `public sealed partial class JobRunMaintainer { public Task PurgeAsync(CancellationToken cancellationToken); }` taking `(AppDbContext, IClock, IOptions<MonitoringOptions>, ILogger<JobRunMaintainer>)`. `RetentionJob` gains it as a fifth constructor parameter, after `ActivationTokenMaintainer`.

- [ ] **Step 1: Write the failing test**

```csharp
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
            context, Clock, Options.Create(new MonitoringOptions { JobRunRetentionDays = 30 }),
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
            context, Clock, Options.Create(new MonitoringOptions()), NullLogger<JobRunMaintainer>.Instance);

        await maintainer.PurgeAsync(CancellationToken.None);
        await maintainer.PurgeAsync(CancellationToken.None);

        Assert.Equal([1], await RemainingAgesAsync("renewal"));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~JobRunMaintainerTests"`
Expected: build error — `JobRunMaintainer` missing.

- [ ] **Step 3: Create `JobRunMaintainer`**

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Monitoring;

/// <summary>
/// The retention half of M6 (spec §3): job runs older than <see cref="MonitoringOptions.JobRunRetentionDays"/>
/// are deleted, <b>except the newest run of every job</b>, so a job that ran once in the spring still
/// shows its last outcome in the autumn rather than "never ran". Batched so a long-unswept table
/// never takes one lock; idempotent and non-fatal like the other maintainers.
/// </summary>
public sealed partial class JobRunMaintainer
{
    private const int BatchSize = 5000;

    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;
    private readonly ILogger<JobRunMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Job run purge removed {RemovedCount} run(s) older than {RetentionDays} day(s).")]
    private static partial void LogPurged(ILogger logger, int removedCount, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Job run purge failed; old runs remain until the next sweep.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    public JobRunMaintainer(
        AppDbContext dbContext, IClock clock, IOptions<MonitoringOptions> options, ILogger<JobRunMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        int retentionDays = _options.Value.JobRunRetentionDays;
        DateTimeOffset cutoff = _clock.UtcNow.AddDays(-retentionDays);
        int removedTotal = 0;

        try
        {
            int removed;
            do
            {
                removed = await _dbContext.Database.ExecuteSqlAsync($"""
                    DELETE TOP ({BatchSize}) r
                    FROM platform.job_runs r
                    WHERE r.started_at_utc < {cutoff}
                      AND EXISTS (SELECT 1 FROM platform.job_runs newer
                                  WHERE newer.job_name = r.job_name
                                    AND newer.started_at_utc > r.started_at_utc)
                    """, cancellationToken);
                removedTotal += removed;
            }
            while (removed == BatchSize);

            LogPurged(_logger, removedTotal, retentionDays);
        }
        catch (SqlException exception) { LogPurgeFailed(_logger, exception); }
        catch (InvalidOperationException exception) { LogPurgeFailed(_logger, exception); }
    }
}
```

Note: `{BatchSize}` inside `ExecuteSqlAsync`'s interpolated string becomes a parameter, and `DELETE TOP (@p0)` is legal T-SQL.

- [ ] **Step 4: Add the fifth step to `RetentionJob`**

Add field, constructor parameter (last), and call (last):

```csharp
    private readonly JobRunMaintainer _jobRuns;
    // ctor: ..., ActivationTokenMaintainer activationTokens, JobRunMaintainer jobRuns)
    //   _jobRuns = jobRuns;
```

At the end of `ExecuteAsync`:

```csharp
        // M6: job-run history on its window, newest run per job kept. This run records itself like any
        // other, and the sweep deletes its own predecessors — a retention job with no visible history
        // would be the one job whose health nobody could see.
        await _jobRuns.PurgeAsync(cancellationToken);
```

Update the class summary's first paragraph to name five halves rather than two. Add `using GymOS.Platform.Infrastructure.Monitoring;`.

In `AddPlatformMonitoring`, add `services.TryAddScoped<JobRunMaintainer>();`.

In `RetentionJobTests`, the `new RetentionJob(...)` call gains a fifth argument:

```csharp
            new JobRunMaintainer(
                context, Clock, Options.Create(new MonitoringOptions()), NullLogger<JobRunMaintainer>.Instance)
```

with `using GymOS.Platform.Application.Monitoring;` and `using GymOS.Platform.Infrastructure.Monitoring;`.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~JobRunMaintainerTests|FullyQualifiedName~RetentionJobTests"`
Expected: all PASS.

- [ ] **Step 6: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Infrastructure tests/GymOS.IntegrationTests/JobRunMaintainerTests.cs tests/GymOS.IntegrationTests/RetentionJobTests.cs
git commit -m "monitoring: RetentionJob sweeps job runs past the window, keeping the newest per job

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 7: The `monitoring.read` permission and its grants

**Files:**
- Modify: `src/Platform/GymOS.Platform.Domain/Identity/PlatformPermissions.cs`
- Modify: `src/Platform/GymOS.Platform.Domain/Identity/SystemRoles.cs`
- Test: `tests/GymOS.Platform.Domain.UnitTests/RoleTests.cs` (existing `SystemRoleDefinitions_ReferenceOnlyDeclaredPermissions` covers the catalog link)

**Interfaces:**
- Produces: `PlatformPermissions.MonitoringRead == "monitoring.read"`.

- [ ] **Step 1: Add the constant and catalog entry**

After `AnalyticsRead` in `PlatformPermissions`:

```csharp
    /// <summary>
    /// The monitoring surface (§3.7): health, job runs, queue depth. Granted to SupportAgent, unlike
    /// <see cref="AnalyticsRead"/>: a support agent answering "why has this gym not renewed?" needs to
    /// see that <c>renewal</c> last succeeded an hour ago, and nothing here is commercially sensitive
    /// the way MRR is. The one field that could carry too much, a run's error text, is trimmed to type
    /// and message before it is stored (M6 spec §2), which is what makes this grant safe.
    /// </summary>
    public const string MonitoringRead = "monitoring.read";
```

In `All`, after the `AnalyticsRead` entry:

```csharp
        new(MonitoringRead, ModuleName, "View platform health, job runs and queue depth"),
```

- [ ] **Step 2: Grant it**

In `SystemRoles.Definitions`, add `PlatformPermissions.MonitoringRead,` as the last entry of the PlatformAdmin list, the SupportAgent list, and the ReadOnly list (each with a one-line comment: `// M6: operational state is not commercially sensitive; see PlatformPermissions.MonitoringRead.`). BillingAdmin does not get it (nothing in a billing role's job needs job history).

- [ ] **Step 3: Run the domain tests and the permission seeder's integration test**

Run: `dotnet test tests/GymOS.Platform.Domain.UnitTests && dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~PermissionSync|FullyQualifiedName~Seeder|FullyQualifiedName~RoleApi"`
Expected: PASS. (If the second filter matches nothing, run `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~Role"` instead — the point is to see the catalog sync accept a new key.)

- [ ] **Step 4: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Domain/Identity
git commit -m "identity: monitoring.read, granted to PlatformAdmin, SupportAgent and ReadOnly

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 8: Application — contracts, `IMonitoringQueries`, `IProcessMetrics`, the stale rule, and the three queries

**Files:**
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/MonitoringContracts.cs`
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/IMonitoringQueries.cs`
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/Abstractions/IProcessMetrics.cs`
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/JobStateAssembler.cs`
- Create: `src/Platform/GymOS.Platform.Application/Monitoring/MonitoringQueriesHandlers.cs`
- Test: `tests/GymOS.Platform.Application.UnitTests/JobStateAssemblerTests.cs`, `tests/GymOS.Platform.Application.UnitTests/MonitoringQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `IRegisteredJobs`/`RegisteredJob` (Task 3), `JobSchedule.Period/Describe` (Task 1), `IDashboardHealthCheck`/`DashboardHealth` (existing), `MonitoringOptions` (Task 5), `PlatformPermissions.MonitoringRead` (Task 7), `JobRunStatus` (Task 4).
- Produces (all `public`, namespace `GymOS.Platform.Application.Monitoring.Abstractions` unless noted):
  ```csharp
  public sealed record JobRunDto(Guid Id, string JobName, string Instance, string Status, DateTimeOffset StartedAtUtc, DateTimeOffset? FinishedAtUtc, long? DurationMs, string? Error);
  public sealed record JobHistoryHead(JobRunDto LatestRun, DateTimeOffset? LastSucceededAtUtc);
  public sealed record JobStateDto(string Name, string Schedule, JobRunDto? LastRun, DateTimeOffset? LastSucceededAtUtc, DateTimeOffset NextDueUtc, bool Stale);
  public sealed record OutboxDepth(long TotalMessages, long MessagesLast24h, long? OldestMessageAgeSeconds, string Note);
  public sealed record ProcessMetrics(double? CpuPercent, long WorkingSetMb, long UptimeSeconds, string Instance, DateTimeOffset SampledAtUtc);
  public sealed record MonitoringOverview(DashboardHealth Health, IReadOnlyList<JobStateDto> Jobs, OutboxDepth Outbox, ProcessMetrics? Process);
  public sealed record JobRunsPage(IReadOnlyList<JobRunDto> Runs, bool HasMore);
  public sealed record JobRunFilter(string JobName, DateTimeOffset? From, DateTimeOffset? To, JobRunStatus? Status, int Take);
  public interface IMonitoringQueries {
      Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken ct);
      Task<bool> HasRunsAsync(string jobName, CancellationToken ct);
      Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken ct);   // newest first, at most filter.Take
      Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken ct);
  }
  public interface IProcessMetrics { ProcessMetrics? Current { get; } }
  // namespace GymOS.Platform.Application.Monitoring
  public static class JobStateAssembler {
      public static bool IsStale(DateTimeOffset now, TimeSpan period, TimeSpan grace, JobHistoryHead? head);
      public static IReadOnlyList<JobStateDto> Assemble(IReadOnlyList<RegisteredJob> jobs, IReadOnlyDictionary<string, JobHistoryHead> heads, DateTimeOffset now, TimeSpan grace);
  }
  public sealed record GetMonitoringOverviewQuery : IQuery<Result<MonitoringOverview>>;
  public sealed record GetJobStatesQuery : IQuery<Result<IReadOnlyList<JobStateDto>>>;
  public sealed record ListJobRunsQuery(string JobName, DateTimeOffset? From, DateTimeOffset? To, string? Status, int Limit) : IQuery<Result<JobRunsPage>>;
  ```
  `OutboxDepth.Note` is always the constant `OutboxDepth.PendingProcessorNote` (defined on the record).

- [ ] **Step 1: Write the failing stale-rule tests**

```csharp
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

public sealed class JobStateAssemblerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static JobRunDto Run(string status, DateTimeOffset startedAt) =>
        new(Guid.NewGuid(), "renewal", "host", status, startedAt, status == "Running" ? null : startedAt.AddSeconds(3), 3000, null);

    [Fact]
    public void AJobThatHasNeverRunIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace, head: null));

    [Fact]
    public void AFreshSuccessIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Succeeded", Now.AddMinutes(-10)), Now.AddMinutes(-10))));

    [Fact]
    public void ASuccessOlderThanTwoPeriodsPlusGraceIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), Now.AddMinutes(-36))));

    [Fact]
    public void ASuccessJustInsideTwoPeriodsPlusGraceIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), Now.AddMinutes(-34))));

    [Fact]
    public void ARunStillRunningWithinPeriodPlusGraceIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Running", Now.AddMinutes(-19)), Now.AddMinutes(-30))));

    [Fact]
    public void ARunStillRunningPastPeriodPlusGraceIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Running", Now.AddMinutes(-21)), Now.AddMinutes(-30))));

    [Fact]
    public void AJobThatHasRunButNeverSucceededIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), LastSucceededAtUtc: null)));

    [Fact]
    public void AssembleListsEveryRegisteredJobEvenWithoutHistory()
    {
        var jobs = new[]
        {
            new GymOS.BuildingBlocks.Application.Jobs.RegisteredJob("renewal", GymOS.BuildingBlocks.Application.Jobs.JobSchedule.Every(Period)),
            new GymOS.BuildingBlocks.Application.Jobs.RegisteredJob("retention", GymOS.BuildingBlocks.Application.Jobs.JobSchedule.DailyAtUtc(new TimeOnly(3, 10))),
        };
        var heads = new Dictionary<string, JobHistoryHead>
        {
            ["renewal"] = new(Run("Succeeded", Now.AddMinutes(-2)), Now.AddMinutes(-2)),
        };

        IReadOnlyList<JobStateDto> states = JobStateAssembler.Assemble(jobs, heads, Now, Grace);

        Assert.Equal(2, states.Count);
        JobStateDto renewal = states.Single(state => state.Name == "renewal");
        Assert.Equal("every 15m", renewal.Schedule);
        Assert.NotNull(renewal.LastRun);
        Assert.False(renewal.Stale);
        Assert.Equal(Now.AddMinutes(15), renewal.NextDueUtc);
        JobStateDto retention = states.Single(state => state.Name == "retention");
        Assert.Null(retention.LastRun);
        Assert.Null(retention.LastSucceededAtUtc);
        Assert.False(retention.Stale);
        Assert.Equal("daily at 03:10 UTC", retention.Schedule);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests --filter "FullyQualifiedName~JobStateAssemblerTests"`
Expected: build error.

- [ ] **Step 3: Create the contracts and abstractions**

`Abstractions/MonitoringContracts.cs`:

```csharp
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Monitoring;

namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>One recorded run. <c>Status</c> is the enum name, <c>Error</c> is type and message only.</summary>
public sealed record JobRunDto(
    Guid Id,
    string JobName,
    string Instance,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long? DurationMs,
    string? Error);

/// <summary>The head of one job's history: its newest run, and when it last succeeded (any run).</summary>
public sealed record JobHistoryHead(JobRunDto LatestRun, DateTimeOffset? LastSucceededAtUtc);

/// <summary>One registered job as the console shows it. <c>Stale</c> is computed on read; nothing stores it.</summary>
public sealed record JobStateDto(
    string Name,
    string Schedule,
    JobRunDto? LastRun,
    DateTimeOffset? LastSucceededAtUtc,
    DateTimeOffset NextDueUtc,
    bool Stale);

/// <summary>
/// Depth only (M6 spec §1, §4): there is no outbox processor and no delivery row yet, so "lag" would be
/// the age of the oldest message ever written. <see cref="Note"/> says so in the payload.
/// </summary>
public sealed record OutboxDepth(long TotalMessages, long MessagesLast24h, long? OldestMessageAgeSeconds, string Note)
{
    public const string PendingProcessorNote =
        "Depth only. Delivery lag and dead letters are reported once the outbox processor exists.";
}

/// <summary>One replica's view of its own process. <c>CpuPercent</c> is null until two samples exist.</summary>
public sealed record ProcessMetrics(
    double? CpuPercent, long WorkingSetMb, long UptimeSeconds, string Instance, DateTimeOffset SampledAtUtc);

public sealed record MonitoringOverview(
    DashboardHealth Health, IReadOnlyList<JobStateDto> Jobs, OutboxDepth Outbox, ProcessMetrics? Process);

/// <summary>Newest first. <c>HasMore</c> means older runs exist before the last one returned.</summary>
public sealed record JobRunsPage(IReadOnlyList<JobRunDto> Runs, bool HasMore);

/// <summary>The history page's filter. <c>Take</c> is already clamped by the handler.</summary>
public sealed record JobRunFilter(string JobName, DateTimeOffset? From, DateTimeOffset? To, JobRunStatus? Status, int Take);
```

`Abstractions/IMonitoringQueries.cs`:

```csharp
namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>Live reads over <c>platform.job_runs</c> and <c>outbox.messages</c> (M6 spec §4–5). Nothing here is a read model.</summary>
public interface IMonitoringQueries
{
    /// <summary>For every job name that has rows: its newest run and its last success. Keyed by job name.</summary>
    Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken);

    Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>Newest first, at most <see cref="JobRunFilter.Take"/> rows.</summary>
    Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken);

    Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
```

`Abstractions/IProcessMetrics.cs`:

```csharp
namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>
/// The API process's own last reading. Its own seam for the same reason as <c>IDashboardHealthCheck</c>:
/// the sampler is a host-side <c>BackgroundService</c> over <c>System.Diagnostics.Process</c>, registered
/// by hand in <c>Program.cs</c>. Null until the first sample has been taken.
/// </summary>
public interface IProcessMetrics
{
    ProcessMetrics? Current { get; }
}
```

- [ ] **Step 4: Create `JobStateAssembler`**

```csharp
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Monitoring;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>
/// Composes the registered jobs with the heads of their histories (M6 spec §4). Pure: everything it
/// needs arrives as a parameter, so the stale rule is unit-tested against a fixed clock.
/// </summary>
public static class JobStateAssembler
{
    /// <summary>
    /// Stale when (a) the newest run is still Running past one period plus grace, or (b) there has been
    /// no success within two periods plus grace. A job with no history is not stale — it has had no
    /// chance yet; a job that has run and never succeeded is, because it has.
    /// </summary>
    public static bool IsStale(DateTimeOffset now, TimeSpan period, TimeSpan grace, JobHistoryHead? head)
    {
        if (head is null)
        {
            return false;
        }

        if (head.LatestRun.Status == nameof(JobRunStatus.Running)
            && now > head.LatestRun.StartedAtUtc + period + grace)
        {
            return true;
        }

        if (head.LastSucceededAtUtc is not DateTimeOffset lastSuccess)
        {
            return true;
        }

        return now > lastSuccess + period + period + grace;
    }

    public static IReadOnlyList<JobStateDto> Assemble(
        IReadOnlyList<RegisteredJob> jobs,
        IReadOnlyDictionary<string, JobHistoryHead> heads,
        DateTimeOffset now,
        TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(heads);

        return jobs
            .Select(job =>
            {
                heads.TryGetValue(job.Name, out JobHistoryHead? head);
                return new JobStateDto(
                    job.Name,
                    job.Schedule.Describe(),
                    head?.LatestRun,
                    head?.LastSucceededAtUtc,
                    job.Schedule.NextAfter(now),
                    IsStale(now, job.Schedule.Period, grace, head));
            })
            .ToList();
    }
}
```

- [ ] **Step 5: Run the assembler tests**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests --filter "FullyQualifiedName~JobStateAssemblerTests"`
Expected: 8 PASS.

- [ ] **Step 6: Write the failing handler tests**

`MonitoringQueryHandlerTests.cs` — the list query's validation and not-found paths through the handler with a fake `IMonitoringQueries` (the Application.UnitTests project already constructs handlers directly; follow `InvoiceCommandTests` for how a handler is `new`ed):

```csharp
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Monitoring;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

public sealed class MonitoringQueryHandlerTests
{
    private sealed class FakeMonitoringQueries : IMonitoringQueries
    {
        public HashSet<string> JobsWithRuns { get; } = [];

        public JobRunFilter? LastFilter { get; private set; }

        public int RunsToReturn { get; set; }

        public Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, JobHistoryHead>>(new Dictionary<string, JobHistoryHead>());

        public Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken) =>
            Task.FromResult(JobsWithRuns.Contains(jobName));

        public Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken)
        {
            LastFilter = filter;
            IReadOnlyList<JobRunDto> runs = Enumerable.Range(0, RunsToReturn)
                .Select(i => new JobRunDto(Guid.NewGuid(), filter.JobName, "h", "Succeeded", DateTimeOffset.UtcNow.AddMinutes(-i), null, null, null))
                .ToList();
            return Task.FromResult(runs);
        }

        public Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new OutboxDepth(0, 0, null, OutboxDepth.PendingProcessorNote));
    }

    private sealed class FakeRegisteredJobs : GymOS.BuildingBlocks.Application.Jobs.IRegisteredJobs
    {
        public IReadOnlyList<GymOS.BuildingBlocks.Application.Jobs.RegisteredJob> All { get; } =
        [
            new("renewal", GymOS.BuildingBlocks.Application.Jobs.JobSchedule.Every(TimeSpan.FromMinutes(15))),
        ];
    }

    private static ListJobRunsQueryHandler Handler(FakeMonitoringQueries queries) =>
        new(queries, new FakeRegisteredJobs());

    [Fact]
    public async Task AnUnknownJobIsNotFound()
    {
        Result<JobRunsPage> result = await Handler(new FakeMonitoringQueries())
            .HandleAsync(new ListJobRunsQuery("nope", null, null, null, 50), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("monitoring.job_not_found", result.Error.Code);
    }

    [Fact]
    public async Task ARenamedJobWithHistoryIsStillReadable()
    {
        var queries = new FakeMonitoringQueries();
        queries.JobsWithRuns.Add("old-name");

        Result<JobRunsPage> result = await Handler(queries)
            .HandleAsync(new ListJobRunsQuery("old-name", null, null, null, 50), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AnInvalidStatusIsAValidationError()
    {
        Result<JobRunsPage> result = await Handler(new FakeMonitoringQueries())
            .HandleAsync(new ListJobRunsQuery("renewal", null, null, "Exploded", 50), CancellationToken.None);

        Assert.Equal("monitoring.invalid_status", result.Error.Code);
    }

    [Fact]
    public async Task FromAfterToIsAValidationError()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Result<JobRunsPage> result = await Handler(new FakeMonitoringQueries())
            .HandleAsync(new ListJobRunsQuery("renewal", now, now.AddHours(-1), null, 50), CancellationToken.None);

        Assert.Equal("monitoring.invalid_range", result.Error.Code);
    }

    [Fact]
    public async Task TheLimitIsClampedAndHasMoreFlipsAtTheBoundary()
    {
        var queries = new FakeMonitoringQueries { RunsToReturn = 201 };

        Result<JobRunsPage> result = await Handler(queries)
            .HandleAsync(new ListJobRunsQuery("renewal", null, null, "Succeeded", 5000), CancellationToken.None);

        Assert.Equal(201, queries.LastFilter!.Take); // limit 200, plus one to detect more
        Assert.Equal(JobRunStatus.Succeeded, queries.LastFilter.Status);
        Assert.Equal(200, result.Value.Runs.Count);
        Assert.True(result.Value.HasMore);
    }
}
```

- [ ] **Step 7: Run to verify they fail**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests --filter "FullyQualifiedName~MonitoringQueryHandlerTests"`
Expected: build error — queries and handler missing.

- [ ] **Step 8: Create the queries and handlers**

`MonitoringQueriesHandlers.cs`:

```csharp
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Monitoring;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>Health, job state, outbox depth and this replica's process, in one document (M6 spec §4).</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record GetMonitoringOverviewQuery : IQuery<Result<MonitoringOverview>>;

/// <summary>The overview's jobs block on its own, for a page that polls job state.</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record GetJobStatesQuery : IQuery<Result<IReadOnlyList<JobStateDto>>>;

/// <summary>One job's run history, newest first, paged by time (M6 spec §5).</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record ListJobRunsQuery(
    string JobName, DateTimeOffset? From, DateTimeOffset? To, string? Status, int Limit) : IQuery<Result<JobRunsPage>>;

internal sealed class GetMonitoringOverviewQueryHandler : IQueryHandler<GetMonitoringOverviewQuery, Result<MonitoringOverview>>
{
    private readonly IDashboardHealthCheck _healthCheck;
    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;
    private readonly IProcessMetrics _processMetrics;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;

    public GetMonitoringOverviewQueryHandler(
        IDashboardHealthCheck healthCheck,
        IMonitoringQueries queries,
        IRegisteredJobs registeredJobs,
        IProcessMetrics processMetrics,
        IClock clock,
        IOptions<MonitoringOptions> options)
    {
        _healthCheck = healthCheck;
        _queries = queries;
        _registeredJobs = registeredJobs;
        _processMetrics = processMetrics;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<MonitoringOverview>> HandleAsync(GetMonitoringOverviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset now = _clock.UtcNow;
        DashboardHealth health = await _healthCheck.CheckAsync(cancellationToken);
        IReadOnlyDictionary<string, JobHistoryHead> heads = await _queries.GetJobHistoryHeadsAsync(cancellationToken);
        OutboxDepth outbox = await _queries.GetOutboxDepthAsync(now, cancellationToken);

        return Result.Success(new MonitoringOverview(
            health,
            JobStateAssembler.Assemble(_registeredJobs.All, heads, now, _options.Value.StaleGrace),
            outbox,
            _processMetrics.Current));
    }
}

internal sealed class GetJobStatesQueryHandler : IQueryHandler<GetJobStatesQuery, Result<IReadOnlyList<JobStateDto>>>
{
    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;

    public GetJobStatesQueryHandler(
        IMonitoringQueries queries, IRegisteredJobs registeredJobs, IClock clock, IOptions<MonitoringOptions> options)
    {
        _queries = queries;
        _registeredJobs = registeredJobs;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<IReadOnlyList<JobStateDto>>> HandleAsync(GetJobStatesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset now = _clock.UtcNow;
        IReadOnlyDictionary<string, JobHistoryHead> heads = await _queries.GetJobHistoryHeadsAsync(cancellationToken);

        return Result.Success(JobStateAssembler.Assemble(_registeredJobs.All, heads, now, _options.Value.StaleGrace));
    }
}

internal sealed class ListJobRunsQueryHandler : IQueryHandler<ListJobRunsQuery, Result<JobRunsPage>>
{
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;

    public ListJobRunsQueryHandler(IMonitoringQueries queries, IRegisteredJobs registeredJobs)
    {
        _queries = queries;
        _registeredJobs = registeredJobs;
    }

    public async Task<Result<JobRunsPage>> HandleAsync(ListJobRunsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.From is DateTimeOffset from && query.To is DateTimeOffset to && from > to)
        {
            return Result.Failure<JobRunsPage>(Error.Validation("monitoring.invalid_range", "`from` must not be after `to`."));
        }

        JobRunStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse(query.Status, ignoreCase: true, out JobRunStatus parsed) || !Enum.IsDefined(parsed))
            {
                return Result.Failure<JobRunsPage>(Error.Validation(
                    "monitoring.invalid_status", "`status` must be one of Running, Succeeded, Failed."));
            }

            status = parsed;
        }

        // A registered job is always addressable, even before its first run; a renamed or removed job
        // stays addressable while its rows last, so history is never orphaned behind a 404.
        bool known = _registeredJobs.All.Any(job => string.Equals(job.Name, query.JobName, StringComparison.Ordinal))
            || await _queries.HasRunsAsync(query.JobName, cancellationToken);
        if (!known)
        {
            return Result.Failure<JobRunsPage>(Error.NotFound(
                "monitoring.job_not_found", $"No job named '{query.JobName}' is registered or has run history."));
        }

        int limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, MaxLimit);

        // One more than asked: the presence of that row is `hasMore`, without a second COUNT query.
        IReadOnlyList<JobRunDto> runs = await _queries.ListRunsAsync(
            new JobRunFilter(query.JobName, query.From, query.To, status, limit + 1), cancellationToken);

        bool hasMore = runs.Count > limit;

        return Result.Success(new JobRunsPage(hasMore ? runs.Take(limit).ToList() : runs, hasMore));
    }
}
```

The Application unit-test project needs to construct `ListJobRunsQueryHandler`, which is `internal`. Check `src/Platform/GymOS.Platform.Application/AssemblyMarker.cs` (or the csproj) for an `InternalsVisibleTo("GymOS.Platform.Application.UnitTests")`; if there is none, look at how `InvoiceCommandTests` reaches its handler and follow that. If the existing tests reach handlers only through a dispatcher, make this one handler `public sealed` with a one-line comment saying why (the FeatureExceptionMaintainer precedent).

- [ ] **Step 9: Run all Application unit tests**

Run: `dotnet test tests/GymOS.Platform.Application.UnitTests`
Expected: all PASS, including `AuthorizationBehaviorTests` (which may enumerate every `IQuery` for a `[RequiresPermission]` — the three queries carry one).

- [ ] **Step 10: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Application/Monitoring tests/GymOS.Platform.Application.UnitTests/JobStateAssemblerTests.cs tests/GymOS.Platform.Application.UnitTests/MonitoringQueryHandlerTests.cs
git commit -m "monitoring: contracts, the stale rule, and the three monitoring queries

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 9: Infrastructure — `MonitoringQueries` over `job_runs` and `outbox.messages`

**Files:**
- Create: `src/Platform/GymOS.Platform.Infrastructure/Persistence/Queries/MonitoringQueries.cs`
- Modify: `src/Platform/GymOS.Platform.Infrastructure/DependencyInjection/PlatformMonitoringServiceCollectionExtensions.cs`
- Test: covered end to end by `MonitoringApiTests` in Task 10 (SQL counts need SQL; a fake would prove the fake). This task ends with a compile + DI check.

**Interfaces:**
- Consumes: `IMonitoringQueries`, DTOs (Task 8); `JobRun` (Task 4); `OutboxMessage` (existing, `GymOS.BuildingBlocks.Infrastructure.Outbox`).

- [ ] **Step 1: Implement**

```csharp
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

/// <summary>
/// The monitoring page's live reads (M6 spec §4–5). Every query is O(jobs) or one aggregate over an
/// indexed column; nothing here is a read model. See <see cref="IMonitoringQueries"/>.
/// </summary>
internal sealed class MonitoringQueries : IMonitoringQueries
{
    private readonly AppDbContext _dbContext;

    public MonitoringQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken)
    {
        IQueryable<JobRun> runs = _dbContext.Set<JobRun>().AsNoTracking();

        // Newest run per job: group, take the max start, join back. Two round trips over the same
        // index rather than a window function EF cannot express portably.
        var newest = await runs
            .GroupBy(run => run.JobName)
            .Select(group => new { JobName = group.Key, StartedAtUtc = group.Max(run => run.StartedAtUtc) })
            .ToListAsync(cancellationToken);

        var lastSuccess = await runs
            .Where(run => run.Status == JobRunStatus.Succeeded)
            .GroupBy(run => run.JobName)
            .Select(group => new { JobName = group.Key, StartedAtUtc = group.Max(run => run.StartedAtUtc) })
            .ToDictionaryAsync(row => row.JobName, row => row.StartedAtUtc, StringComparer.Ordinal, cancellationToken);

        var heads = new Dictionary<string, JobHistoryHead>(StringComparer.Ordinal);
        foreach (var head in newest)
        {
            JobRun latest = await runs
                .Where(run => run.JobName == head.JobName && run.StartedAtUtc == head.StartedAtUtc)
                .OrderByDescending(run => run.Id)
                .FirstAsync(cancellationToken);

            heads[head.JobName] = new JobHistoryHead(
                ToDto(latest),
                lastSuccess.TryGetValue(head.JobName, out DateTimeOffset succeededAt) ? succeededAt : null);
        }

        return heads;
    }

    public Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken) =>
        _dbContext.Set<JobRun>().AsNoTracking().AnyAsync(run => run.JobName == jobName, cancellationToken);

    public async Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<JobRun> query = _dbContext.Set<JobRun>().AsNoTracking()
            .Where(run => run.JobName == filter.JobName);

        if (filter.From is DateTimeOffset from)
        {
            query = query.Where(run => run.StartedAtUtc >= from);
        }

        if (filter.To is DateTimeOffset to)
        {
            query = query.Where(run => run.StartedAtUtc <= to);
        }

        if (filter.Status is JobRunStatus status)
        {
            query = query.Where(run => run.Status == status);
        }

        List<JobRun> runs = await query
            .OrderByDescending(run => run.StartedAtUtc)
            .ThenByDescending(run => run.Id)
            .Take(filter.Take)
            .ToListAsync(cancellationToken);

        return runs.Select(ToDto).ToList();
    }

    public async Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<OutboxMessage> messages = _dbContext.Set<OutboxMessage>().AsNoTracking();
        DateTimeOffset dayAgo = now.AddHours(-24);

        long total = await messages.LongCountAsync(cancellationToken);
        long last24h = await messages.LongCountAsync(message => message.CreatedAtUtc >= dayAgo, cancellationToken);
        DateTimeOffset? oldest = total == 0
            ? null
            : await messages.MinAsync(message => (DateTimeOffset?)message.CreatedAtUtc, cancellationToken);

        return new OutboxDepth(
            total,
            last24h,
            oldest is DateTimeOffset o ? (long)(now - o).TotalSeconds : null,
            OutboxDepth.PendingProcessorNote);
    }

    private static JobRunDto ToDto(JobRun run) => new(
        run.Id,
        run.JobName,
        run.Instance,
        run.Status.ToString(),
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.DurationMs,
        run.Error);
}
```

In `AddPlatformMonitoring`, add `services.TryAddScoped<IMonitoringQueries, MonitoringQueries>();` (with `using GymOS.Platform.Application.Monitoring.Abstractions;` and `using GymOS.Platform.Infrastructure.Persistence.Queries;`).

- [ ] **Step 2: Build**

Run: `dotnet build GymOS.Backend.slnx -c Release`
Expected: clean.

- [ ] **Step 3: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Platform/GymOS.Platform.Infrastructure
git commit -m "monitoring: MonitoringQueries — history heads, run pages, outbox depth

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 10: Host — process sampler, endpoints, OpenAPI snapshot, API tests

**Files:**
- Create: `src/Host/GymOS.WebApi/Monitoring/ProcessMetricsSampler.cs`
- Create: `src/Host/GymOS.WebApi/Endpoints/MonitoringEndpoints.cs`
- Modify: `src/Host/GymOS.WebApi/Program.cs` (register sampler near line 92; `MapMonitoringEndpoints` after line 339)
- Modify: the OpenAPI snapshot file `OpenApiContractTests.SnapshotFile()` points at (regenerated)
- Test: `tests/GymOS.IntegrationTests/MonitoringApiTests.cs`

**Interfaces:**
- Consumes: `IProcessMetrics`/`ProcessMetrics` (Task 8), queries (Task 8), `PlatformPermissions.MonitoringRead` (Task 7).
- Produces: routes `GET /api/v1/monitoring/overview`, `GET /api/v1/monitoring/jobs`, `GET /api/v1/monitoring/jobs/{name}/runs?from=&to=&status=&limit=`.

- [ ] **Step 1: Write the failing API tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The monitoring surface over real HTTP (M6 spec §4–6): the empty platform is a valid platform, the
/// permission boundary is the opposite of analytics' (SupportAgent may read), history pages by time,
/// and an unknown job is a 404. Runs are seeded through the DbContext — the endpoints read rows, so
/// rows are the honest unit of test here, as <c>AnalyticsApiTests</c> seeds its read model.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MonitoringApiTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public MonitoringApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("monitoring_api");
        // The host's timer stays off so the registered jobs do not write rows under the assertions.
        _host = AuthenticationHost.Create(connectionString, settings => settings["Jobs:Enabled"] = "false");
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task TheEmptyPlatformIsAValidOverview()
    {
        JsonElement overview = await GetAsync("/api/v1/monitoring/overview");

        Assert.Equal("Healthy", overview.GetProperty("health").GetProperty("status").GetString());
        JsonElement jobs = overview.GetProperty("jobs");
        Assert.True(jobs.GetArrayLength() >= 5); // renewal, invoice overdue, retention, stats, feature sweep
        foreach (JsonElement job in jobs.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, job.GetProperty("lastRun").ValueKind);
            Assert.False(job.GetProperty("stale").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(job.GetProperty("schedule").GetString()));
        }

        JsonElement outbox = overview.GetProperty("outbox");
        Assert.Equal(JsonValueKind.Null, outbox.GetProperty("oldestMessageAgeSeconds").ValueKind);
        Assert.Contains("processor", outbox.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecordedRunAppearsAsTheJobsLastRun()
    {
        await SeedAsync("renewal", DateTimeOffset.UtcNow.AddMinutes(-2), JobRunStatus.Failed, "InvalidOperationException: boom");

        JsonElement jobs = await GetAsync("/api/v1/monitoring/jobs");
        JsonElement renewal = jobs.EnumerateArray().Single(job => job.GetProperty("name").GetString() == "renewal");

        Assert.Equal("Failed", renewal.GetProperty("lastRun").GetProperty("status").GetString());
        Assert.Equal("InvalidOperationException: boom", renewal.GetProperty("lastRun").GetProperty("error").GetString());
        Assert.True(renewal.GetProperty("stale").GetBoolean()); // ran, never succeeded
    }

    [Fact]
    public async Task SupportAgentMayReadMonitoring()
    {
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.monitoring@gymos.test");

        using HttpResponseMessage response = await supportAgent.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BillingAdminIsRefused()
    {
        using HttpClient billingAdmin = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "BillingAdmin", "billing.monitoring@gymos.test");

        using HttpResponseMessage response = await billingAdmin.GetAsync(
            new Uri("/api/v1/monitoring/jobs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownJobIs404()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/monitoring/jobs/does-not-exist/runs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RunsAreNewestFirstAndTheLimitIsClamped()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 205; i++)
        {
            await SeedAsync("retention", now.AddMinutes(-i), JobRunStatus.Succeeded, error: null);
        }

        JsonElement page = await GetAsync("/api/v1/monitoring/jobs/retention/runs?limit=500");

        Assert.Equal(200, page.GetProperty("runs").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        DateTimeOffset first = page.GetProperty("runs")[0].GetProperty("startedAtUtc").GetDateTimeOffset();
        DateTimeOffset second = page.GetProperty("runs")[1].GetProperty("startedAtUtc").GetDateTimeOffset();
        Assert.True(first > second);
    }

    [Fact]
    public async Task AnInvalidStatusIs400()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/monitoring/jobs/renewal/runs?status=Exploded", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> GetAsync(string relativeUrl)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(relativeUrl, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedAsync(string jobName, DateTimeOffset startedAt, JobRunStatus status, string? error)
    {
        await using AppDbContext context = _host.CreateDbContext();
        JobRun run = JobRun.Start(Guid.NewGuid(), jobName, "seed", startedAt);
        context.Set<JobRun>().Add(run);
        await context.SaveChangesAsync();

        if (status != JobRunStatus.Running)
        {
            await context.Database.ExecuteSqlAsync($"""
                UPDATE platform.job_runs
                SET status = {(byte)status}, finished_at_utc = {startedAt.AddSeconds(2)}, duration_ms = 2000, error = {error}
                WHERE id = {run.Id}
                """);
        }
    }
}
```

`AuthenticationHost.CreateDbContext()` exists (line 56). Add `using Microsoft.EntityFrameworkCore;` for `ExecuteSqlAsync`. The seeded jobs' names must be real registered names — confirm `"renewal"` and `"retention"` against `RenewalJob.Name` and `RetentionJob.Name` before running.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~MonitoringApiTests"`
Expected: FAIL — 404 on every route (endpoints not mapped).

- [ ] **Step 3: Create the sampler**

```csharp
using System.Diagnostics;
using GymOS.Platform.Application.Monitoring.Abstractions;

namespace GymOS.WebApi.Monitoring;

/// <summary>
/// Samples this process every 15 seconds so <c>/monitoring/overview</c> returns the last reading and
/// never blocks on a measurement (M6 spec §4). CPU is the process-time delta over the interval divided
/// by the core count, so it is null on the first sample. One replica's view: <c>Instance</c> says which.
/// Lives in the host because <see cref="Process"/> is a host concern, like the health adapter.
/// </summary>
internal sealed class ProcessMetricsSampler : BackgroundService, IProcessMetrics
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private volatile ProcessMetrics? _current;

    public ProcessMetricsSampler(TimeProvider time) => _time = time;

    public ProcessMetrics? Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Process process = Process.GetCurrentProcess();
        TimeSpan lastCpu = process.TotalProcessorTime;
        DateTimeOffset lastAt = _time.GetUtcNow();
        Publish(process, cpuPercent: null, lastAt);

        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                process.Refresh();
                TimeSpan cpu = process.TotalProcessorTime;
                DateTimeOffset at = _time.GetUtcNow();

                double elapsedMs = (at - lastAt).TotalMilliseconds;
                double? percent = elapsedMs <= 0
                    ? null
                    : Math.Round((cpu - lastCpu).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100, 1);

                lastCpu = cpu;
                lastAt = at;
                Publish(process, percent, at);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private void Publish(Process process, double? cpuPercent, DateTimeOffset sampledAt) =>
        _current = new ProcessMetrics(
            cpuPercent,
            process.WorkingSet64 / (1024 * 1024),
            (long)(sampledAt - process.StartTime.ToUniversalTime()).TotalSeconds,
            Environment.MachineName,
            sampledAt);
}
```

`TimeProvider` is registered by default in .NET hosts? It is **not** — register `TimeProvider.System` explicitly in `Program.cs` alongside the sampler unless a `TimeProvider` registration already exists (grep `TimeProvider` in `Program.cs` first).

- [ ] **Step 4: Create the endpoints**

```csharp
using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/monitoring</c> — health, job state, outbox depth and this replica's process (ARCHITECTURE.md
/// §3.7, narrowed by the M6 spec: no devices, no logs, no metric history — each deferred to a named
/// increment, none pretended).
/// </summary>
public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Monitoring").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/monitoring")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Monitoring");

        group.MapGet("/overview", OverviewAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("GetMonitoringOverview")
            .WithSummary("Health, job state, outbox depth and this replica's process, live")
            .WithDescription(
                "Four independently sourced blocks. `health` is the same projection `/dashboard/health` "
                    + "returns. `jobs` lists every registered job, including ones that have never run "
                    + "(`lastRun: null`); `stale` is computed on read from the schedule and the run "
                    + "history. `outbox` is depth only — lag and dead letters arrive with the outbox "
                    + "processor, and `note` says so. `process` is the answering replica's own CPU, memory "
                    + "and uptime, null until its first sample; fleet-wide numbers are App Insights'.")
            .Produces<MonitoringOverview>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/jobs", JobsAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("GetJobStates")
            .WithSummary("Every registered job's schedule, last run, next due time and stale flag")
            .WithDescription(
                "The overview's `jobs` block on its own, for a page that polls job state. A job is stale "
                    + "when its newest run is still running past one period plus grace, or when it has "
                    + "not succeeded within two periods plus grace (grace is `Monitoring:StaleGrace`, "
                    + "default 5 minutes). A job that has never run is not stale.")
            .Produces<IReadOnlyList<JobStateDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/jobs/{name}/runs", RunsAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("ListJobRuns")
            .WithSummary("One job's run history, newest first, paged by time")
            .WithDescription(
                "`name` is a registered job, or one that still has history under an old name. `from` "
                    + "and `to` are optional and inclusive on the run's start time; `status` is one of "
                    + "Running, Succeeded, Failed; `limit` defaults to 50 and is clamped to 200. "
                    + "`hasMore` means older runs exist — page by moving `to` to the oldest "
                    + "`startedAtUtc` returned. A run's `error` is the exception's type and message "
                    + "only; the stack is in the structured log.")
            .Produces<JobRunsPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<MonitoringOverview> result =
            await dispatcher.QueryAsync(new GetMonitoringOverviewQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> JobsAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<JobStateDto>> result =
            await dispatcher.QueryAsync(new GetJobStatesQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> RunsAsync(
        string name,
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? status = null,
        int limit = 50)
    {
        Result<JobRunsPage> result =
            await dispatcher.QueryAsync(new ListJobRunsQuery(name, from, to, status, limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
```

- [ ] **Step 5: Wire `Program.cs`**

After the `IDashboardHealthCheck` registration (line 92):

```csharp
    // M6: this replica's own process metrics. Host-side for the same reason as the health adapter
    // (System.Diagnostics.Process is not an Application concern). One instance, both roles.
    builder.Services.AddSingleton<ProcessMetricsSampler>();
    builder.Services.AddSingleton<IProcessMetrics>(provider => provider.GetRequiredService<ProcessMetricsSampler>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ProcessMetricsSampler>());
```

Plus `builder.Services.TryAddSingleton(TimeProvider.System);` if no `TimeProvider` is registered yet (check first). Add `using GymOS.Platform.Application.Monitoring.Abstractions;` and `using GymOS.WebApi.Monitoring;`.

After `app.MapAnalyticsEndpoints();` (line 339): `app.MapMonitoringEndpoints();`.

- [ ] **Step 6: Run the API tests**

Run: `dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~MonitoringApiTests"`
Expected: 7 PASS. If `TheEmptyPlatformIsAValidOverview` fails on the job count, read the registered names from the `jobs` array in the failure output and fix the assertion to the actual count rather than lowering it blindly.

- [ ] **Step 7: Regenerate the OpenAPI snapshot and read the diff**

Run (Git Bash): `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests --filter "FullyQualifiedName~OpenApiContractTests"` then `git diff --stat` and `git diff -- <snapshot path>`.
Expected: the diff adds exactly three paths under `/api/v1/monitoring/…` and the new schemas (`MonitoringOverview`, `JobStateDto`, `JobRunDto`, `OutboxDepth`, `ProcessMetrics`, `JobRunsPage`, plus `DashboardHealth`/`HealthCheckStatus` references). Nothing removed. Then run the contract test again **without** the variable → PASS.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet build GymOS.Backend.slnx -c Release && dotnet test GymOS.Backend.slnx`
Expected: all green. Note the total; STATUS records it.

- [ ] **Step 9: Format and commit**

```bash
dotnet format --verify-no-changes || dotnet format
git add src/Host tests/GymOS.IntegrationTests/MonitoringApiTests.cs <snapshot path>
git commit -m "monitoring: /monitoring/overview, /jobs and /jobs/{name}/runs, with this replica's process metrics

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
```

---

### Task 11: Docs — business rules, release notes, STATUS

**Files:**
- Create: `docs/BusinessRules/Monitoring.md`
- Modify: `docs/RELEASE-NOTES.md` (new top section `## M6 — Monitoring`)
- Modify: `docs/STATUS.md` (resume point)

- [ ] **Step 1: Write `docs/BusinessRules/Monitoring.md`**

Follow `Analytics.md`'s shape (Purpose; numbered sections; Deferred; Known limitations). Content, in prose not bullet-dumps, covering: (1) the host records every run, skipped ticks are not runs, recorder failures never fail a job; (2) the error-text rule and why it makes `monitoring.read` safe for SupportAgent; (3) the stale rule with the exact inequality and the grace default, including the never-succeeded case; (4) retention: 30 days, newest run per job always kept, the SQL predicate; (5) outbox depth only, with the reason in the note; (6) process metrics are one replica's view; (7) endpoints table (method, route, permission, error codes `monitoring.job_not_found`, `monitoring.invalid_status`, `monitoring.invalid_range`); (8) permissions and the SupportAgent argument; (9) deferred table copied from the spec §1/§7; (10) known limitations: a process killed mid-run leaves a `Running` row that reads stale after grace and is never rewritten; `GetJobHistoryHeadsAsync` issues one query per job with history (fine at five jobs; revisit at fifty).

- [ ] **Step 2: Add the release-notes section**

At the top of `docs/RELEASE-NOTES.md`, before `## M3.2`, insert `## M6 — Monitoring` with **Suggested version**, **Status: complete, awaiting review. Business rules: [BusinessRules/Monitoring.md](BusinessRules/Monitoring.md).**, and three or four short subsections: the job host records every run; the monitoring surface (three endpoints, `monitoring.read`); retention; what was deferred and to where.

- [ ] **Step 3: Update STATUS's resume point**

Replace the paragraph beginning `**M6's design spec is written and awaiting the owner's review:**` with a paragraph stating M6 is implemented on `development` at the final commit, the test count from Task 10 Step 8, what CI run to look at, and that **the next increment is M7** (settings with consumed categories only, maintenance mode, API rate limiting, audit CSV export — per the 2026-09-03 decision), then the ~19 non-severe review findings and the gym-scoped-grants spec review. Add a `### M6 — what landed` subsection under the 2026-09-18 resumption with the file map's shape in prose and any deviation from the spec found during implementation (there should be none; if there was one, name it).

- [ ] **Step 4: Commit and push**

```bash
git add docs/BusinessRules/Monitoring.md docs/RELEASE-NOTES.md docs/STATUS.md
git commit -m "docs: M6 monitoring — business rules, release notes, STATUS

Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01W9D8qHarDkZp5B5ZnnJyAE"
git push origin development
```

Then watch `gh run list --limit 2` until CI and Deploy to UAT finish; expect CI green (both jobs) and Deploy to UAT green, with the `migrate` job applying `AddJobRuns` (one retry notice is normal if the database was paused).

---

## Self-review

**Spec coverage.** §1 deferrals → Task 10 endpoint descriptions and Task 11 docs; no devices/logs/snapshots endpoints anywhere. §2 host recording, own scope, skipped ticks not recorded, recorder failure non-fatal, instance, error trimming, `Running` rows left alone, `Period`/`Describe` → Tasks 1, 2, 5. §3 table, index, no row version, no FK, plain entity, migration `AddJobRuns`, retention with newest-per-job kept, batched, `MonitoringOptions` default 30 → Tasks 4, 5, 6. §4 overview blocks: health reused via `IDashboardHealthCheck`, jobs from `IRegisteredJobs` with stale rule and grace, outbox depth with note, process sampler every 15 s with null first CPU → Tasks 3, 8, 9, 10. §5 `/jobs`, `/jobs/{name}/runs` with 404, clamp 1..200 default 50, `hasMore`, no manual-run endpoint → Tasks 8, 10. §6 `monitoring.read` granted to SupportAgent, `/dashboard/health` unchanged → Task 7. §8 verification: unit (host recording ×4, schedule, stale ×7, retention predicate is integration here since it is SQL), integration (end-to-end run, failing run, retention 31/29/400, endpoints 200/403, 404, clamp, hasMore, empty platform) → Tasks 2, 3, 5, 6, 8, 10. Live check is left to the executor's discretion at the end of Task 10 and is not required for merge.

**Placeholders.** None: every step carries its code, command and expected result. Task 11's docs are described by content list rather than full text, which is the intended shape for prose documents the executor writes from the spec.

**Type consistency.** `IJobRunRecorder` signature identical in Tasks 2, 5 and the fakes. `JobHistoryHead(JobRunDto LatestRun, DateTimeOffset? LastSucceededAtUtc)` identical in Tasks 8 and 9. `JobRunFilter.Take` used by handler (limit+1) and query (`Take(filter.Take)`). `JobRunStatus` byte values match the migration transcription (`tinyint`, `HasConversion<byte>()`). `RegisteredJob(string Name, JobSchedule Schedule)` in Tasks 3, 8. `MonitoringOptions.StaleGrace`/`JobRunRetentionDays` in Tasks 5, 6, 8. Route parameter `name` maps to `ListJobRunsQuery.JobName`.
