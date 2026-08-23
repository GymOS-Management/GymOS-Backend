namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>
/// Cross-instance mutual exclusion for one job run.
///
/// <para>Two API replicas (or an API and a future worker host) must not run the same sweep
/// concurrently — not for correctness of the sweeps themselves, which are idempotent, but so a
/// gym is never touched by two racing transactions doing the same renewal. The implementation is
/// a PostgreSQL advisory lock: the database is the one thing every instance already shares, the
/// same reasoning as the outbox's READPAST claiming (AD-13).</para>
/// </summary>
public interface IJobLock
{
    /// <summary>
    /// Tries to take the named lock. Null when another holder has it — the caller skips this
    /// tick. Disposing the handle releases the lock.
    /// </summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken);
}
