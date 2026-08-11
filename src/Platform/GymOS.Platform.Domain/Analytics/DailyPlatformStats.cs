using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Analytics;

/// <summary>The gym counts of one day, passed as a group so a caller cannot get their order wrong.</summary>
public sealed record GymCounts(
    int Total, int Active, int Suspended, int Provisioning, int New, int Canceled, int Expired);

/// <summary>One currency's MRR on one day. Part of the <see cref="DailyPlatformStats"/> aggregate.</summary>
public sealed class PlatformMrr : Entity
{
    internal PlatformMrr(Guid id, Guid statsId, Money amount)
        : base(id)
    {
        StatsId = statsId;
        Amount = amount;
    }

    private PlatformMrr()
    {
    }

    public Guid StatsId { get; private set; }

    public Money Amount { get; private set; }
}

/// <summary>
/// What was true about the platform on one day (ARCHITECTURE.md §2.4's <c>daily_platform_stats</c>,
/// AD-8) — the only source the analytics endpoints read.
///
/// <para><b>Written once, never recomputed.</b> Status counts are point-in-time facts and nothing in
/// the schema records what a gym's status was last Tuesday, so a "recompute" would write today's
/// answer under an old date and call it history. The job inserts when the day is absent and does
/// nothing when it is present; a missed night stays a gap, which is honest, rather than a row that
/// is half-real with nothing on it saying which half.</para>
///
/// <para>Not audited and carrying no concurrency token, unlike every aggregate that holds a
/// decision: this is derived data with exactly one writer, which <c>IJobLock</c> already serialises.
/// The audit trail of a computed row is the job's log.</para>
/// </summary>
public sealed class DailyPlatformStats : AggregateRoot
{
    private readonly List<PlatformMrr> _mrr = [];

    private DailyPlatformStats(Guid id, DateOnly statDate)
        : base(id) => StatDate = statDate;

    private DailyPlatformStats()
    {
    }

    public DateOnly StatDate { get; private set; }

    public int TotalGyms { get; private set; }

    public int ActiveGyms { get; private set; }

    public int SuspendedGyms { get; private set; }

    public int ProvisioningGyms { get; private set; }

    public int NewGyms { get; private set; }

    public int CanceledGyms { get; private set; }

    public int ExpiredGyms { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>One row per currency — see <c>MonthlyRecurringRevenue</c> on why they are not summed.</summary>
    public IReadOnlyCollection<PlatformMrr> Mrr => _mrr.AsReadOnly();

    public static DailyPlatformStats Record(
        DateOnly statDate,
        GymCounts counts,
        IReadOnlyList<Money> mrr,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(mrr);

        if (counts.Total < 0 || counts.Active < 0 || counts.Suspended < 0 || counts.Provisioning < 0
            || counts.New < 0 || counts.Canceled < 0 || counts.Expired < 0)
        {
            throw new DomainException("A platform stat count cannot be negative.");
        }

        // Two rows for one currency would double-count that day on every chart that reads it, and
        // the only way to produce one is a caller that summed wrongly — a defect, not a bad request.
        if (mrr.Select(amount => amount.Currency.Code).Distinct(StringComparer.Ordinal).Count() != mrr.Count)
        {
            throw new DomainException("MRR carries one amount per currency; a currency appeared twice.");
        }

        var stats = new DailyPlatformStats(Guid.NewGuid(), statDate)
        {
            TotalGyms = counts.Total,
            ActiveGyms = counts.Active,
            SuspendedGyms = counts.Suspended,
            ProvisioningGyms = counts.Provisioning,
            NewGyms = counts.New,
            CanceledGyms = counts.Canceled,
            ExpiredGyms = counts.Expired,
            CreatedAtUtc = now,
        };

        foreach (Money amount in mrr)
        {
            stats._mrr.Add(new PlatformMrr(Guid.NewGuid(), stats.Id, amount));
        }

        return stats;
    }
}
