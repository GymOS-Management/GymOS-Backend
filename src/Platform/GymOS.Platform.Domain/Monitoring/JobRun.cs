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
