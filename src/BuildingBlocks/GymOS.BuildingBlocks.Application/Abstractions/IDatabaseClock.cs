namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The database's own "now" — the one clock every instance shares.
///
/// <para>Distinct from <see cref="IClock"/> (this process's clock) on purpose. Request handling
/// reads <see cref="IClock"/>: a few milliseconds of skew cannot hurt a single request. The
/// background sweeps do not get that luxury — they turn "is this past due?" into <b>durable,
/// sometimes terminal</b> state (Overdue, Expired, a billed renewal), and a host with a fast
/// clock would commit those verdicts early against rows whose timestamps the database wrote.
/// Measuring the sweep's questions with the database's clock removes the host's clock from the
/// blast radius entirely — the same reasoning the idempotency store used for its lease checks
/// from day one.</para>
/// </summary>
public interface IDatabaseClock
{
    Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken);
}
