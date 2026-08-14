using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Analytics;

/// <summary>
/// Turns one day into one <see cref="DailyPlatformStats"/> row, or does nothing because the day is
/// already recorded.
///
/// <para>Separate from the job so the decision is testable without a scheduler.</para>
///
/// <para><b>This is not a backfill tool, and it used to say it was.</b> Only the event counts
/// (new/canceled/expired) are windowed to the day; the population counts and MRR are read from live
/// state with no time bound. So compiling a day other than the one that just ended writes *today's*
/// population under that date — and because the row is write-once, it then blocks the correct row
/// from ever being written. "No backfill, and gaps stay gaps" is the rule in the design spec,
/// <c>Analytics.md</c>, <see cref="DailyPlatformStats"/> and the job; the earlier wording here
/// invited the opposite, so <see cref="CompileAsync"/> now refuses a day that has not ended.</para>
/// </summary>
internal sealed class DailyStatsCompiler
{
    private readonly AppDbContext _dbContext;
    private readonly IPlatformStatsRepository _stats;
    private readonly IUnitOfWork _unitOfWork;

    public DailyStatsCompiler(AppDbContext dbContext, IPlatformStatsRepository stats, IUnitOfWork unitOfWork)
    {
        _dbContext = dbContext;
        _stats = stats;
        _unitOfWork = unitOfWork;
    }

    public async Task<bool> CompileAsync(DateOnly statDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The day has to be over. Today's population is not yet a fact about today, and tomorrow's is
        // not a fact about anything — but the unwindowed reads below would happily record either, and
        // the write-once rule would then make it permanent. A forward-skewed database clock is the
        // production route to this: `PlatformStatsJob` derives yesterday from that clock, so a skew
        // makes "yesterday" a date that has not happened.
        DateOnly lastCompleteDay = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1);

        if (statDate > lastCompleteDay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statDate),
                statDate,
                $"A day can only be compiled once it has ended; the last complete day is {lastCompleteDay}.");
        }

        // Write-once: the row is a point-in-time snapshot, so re-running would write today's answer
        // under an old date. Skipping is the whole rule, not an optimisation.
        if (await _stats.ExistsForAsync(statDate, cancellationToken))
        {
            return false;
        }

        DateTimeOffset dayStart = new(statDate, TimeOnly.MinValue, TimeSpan.Zero);
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        IQueryable<Gym> livingGyms = _dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Status != GymStatus.Deleted);

        // Two families of count, and they deliberately read differently:
        //
        // Population counts (Total/Active/Suspended/Provisioning) describe who is a tenant RIGHT
        // NOW, so they run over livingGyms and exclude anything deleted — a deleted gym is not a
        // tenant any more.
        //
        // Event counts (New/Canceled/Expired) record what HAPPENED on this day, regardless of the
        // gym's status now. A gym created and deleted on the same day must still show up as one
        // created — suppressing it would drop the population with nothing in the series to explain
        // why. Canceled/Expired already run unfiltered by gym status for the same reason: deleting a
        // gym cancels its live subscription in the same transaction, and that cancellation is the
        // strongest form of churn there is.
        // One grouped query for the whole population, not four independent counts.
        //
        // Four `CountAsync` calls are four statements, each taking its own READ COMMITTED snapshot, so
        // a gym created between them was counted by some and not the others — and the row that came
        // out broke the identity `Analytics.md` promises: total = active + suspended + provisioning.
        // These rows are write-once, so the contradiction was served forever. One GROUP BY sees a
        // single snapshot, which makes the identity true by construction rather than by luck; the
        // guard in `DailyPlatformStats.Record` is the belt to this braces. It also turns four round
        // trips into one.
        Dictionary<GymStatus, int> population = await livingGyms
            .GroupBy(gym => gym.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

        int Living(GymStatus status) => population.TryGetValue(status, out int count) ? count : 0;

        var counts = new GymCounts(
            Total: population.Values.Sum(),
            Active: Living(GymStatus.Active),
            Suspended: Living(GymStatus.Suspended),
            Provisioning: Living(GymStatus.Provisioning),

            New: await _dbContext.Set<Gym>().AsNoTracking().CountAsync(
                gym => gym.CreatedAtUtc >= dayStart && gym.CreatedAtUtc < dayEnd, cancellationToken),
            // Both churn counts are counts of GYMS, not of subscription rows, and the `Distinct` is
            // what makes them so. A gym can hold any number of terminal rows: the one-live-per-gym
            // unique index is partial (`status IN (1, 2, 3)`), so a cancelled row leaves the
            // constraint behind and the next assignment inserts a fresh one. Counting rows reported a
            // gym that churned twice in a day as two churned gyms — which on a small platform is a
            // churn rate over 100%, written permanently into a write-once table.
            //
            // Not deduplicated *across* the two counts, deliberately: a gym that cancels one
            // subscription and has another expire on the same day churned for two different reasons,
            // and keeping those causes apart is the whole point of splitting the column (D5).
            Canceled: await _dbContext.Set<GymSubscription>().AsNoTracking()
                .Where(subscription => subscription.Status == SubscriptionStatus.Canceled
                    && subscription.CanceledAtUtc >= dayStart && subscription.CanceledAtUtc < dayEnd)
                .Select(subscription => subscription.GymId)
                .Distinct()
                .CountAsync(cancellationToken),

            // Counted by when the expiry was APPLIED (`ExpiredAtUtc`), not by the period end it
            // followed. `Expire` stamps that moment now, exactly as `CancelNow` always has.
            //
            // Keying on `CurrentPeriodEndUtc` was wrong in a way no re-run could repair: `Expire`
            // never touches that column, so an expiry the sweep applied after its own day had been
            // compiled fell into no day at all — the period-end day's row was sealed while the
            // subscription was still live, and the day it actually expired looks for a period end it
            // will never find. Write-once rows meant the event was simply gone from the churn series.
            Expired: await _dbContext.Set<GymSubscription>().AsNoTracking()
                .Where(subscription => subscription.Status == SubscriptionStatus.Expired
                    && subscription.ExpiredAtUtc >= dayStart
                    && subscription.ExpiredAtUtc < dayEnd)
                .Select(subscription => subscription.GymId)
                .Distinct()
                .CountAsync(cancellationToken));

        var contributions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => SubscriptionStatusTransitions.Billing.Contains(subscription.Status))
            .Select(subscription => new { subscription.Status, subscription.BillingCycle, subscription.Price })
            .ToListAsync(cancellationToken);

        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            contributions.Select(row => new MrrContribution(row.Status, row.BillingCycle, row.Price)));

        _stats.Add(DailyPlatformStats.Record(statDate, counts, mrr, now));
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return true;
    }
}
