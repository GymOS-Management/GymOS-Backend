namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>
/// A unit of recurring background work, hosted by the job runner (M3.4; ARCHITECTURE.md's
/// <c>BackgroundJobs/</c>).
///
/// <para><b>Derive work from state, not from the tick.</b> A run can be skipped (another instance
/// held the lock), can fail (the next tick retries), and can fire late (the host was down) — so a
/// correct job re-derives what is due from rows whose columns say so, and running it twice in a
/// row changes nothing the second time. Every M3.4 job is such a sweep.</para>
/// </summary>
public interface IScheduledJob
{
    /// <summary>
    /// Stable name — the cross-instance lock key and the log correlation value. Renaming one
    /// changes its lock identity, so two differently-named deployments could briefly overlap.
    /// </summary>
    string Name { get; }

    JobSchedule Schedule { get; }

    /// <summary>
    /// One run. <paramref name="now"/> is the clock read at the tick's edge (AD-40: time flows
    /// inward as a parameter); a job that needs "now" twice uses this one value.
    /// </summary>
    Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
