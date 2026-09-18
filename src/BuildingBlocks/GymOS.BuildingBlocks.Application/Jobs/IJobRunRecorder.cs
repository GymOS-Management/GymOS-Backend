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
