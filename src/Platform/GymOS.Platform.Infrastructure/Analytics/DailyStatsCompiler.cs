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
/// <para>Separate from the job so the decision is testable without a scheduler, and so the same
/// compilation can be triggered by hand when a gap needs filling deliberately.</para>
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

        var counts = new GymCounts(
            Total: await livingGyms.CountAsync(cancellationToken),
            Active: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Active, cancellationToken),
            Suspended: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Suspended, cancellationToken),
            Provisioning: await livingGyms.CountAsync(gym => gym.Status == GymStatus.Provisioning, cancellationToken),

            // The three event counts are the day's, not the running total — they are what a growth
            // or churn chart plots, and each is dated by a column rather than inferred.
            New: await livingGyms.CountAsync(
                gym => gym.CreatedAtUtc >= dayStart && gym.CreatedAtUtc < dayEnd, cancellationToken),
            Canceled: await _dbContext.Set<GymSubscription>().AsNoTracking().CountAsync(
                subscription => subscription.Status == SubscriptionStatus.Canceled
                    && subscription.CanceledAtUtc >= dayStart && subscription.CanceledAtUtc < dayEnd,
                cancellationToken),

            // Expiry has no timestamp of its own: the renewal sweep expires a subscription *at* its
            // period end, which is therefore the business date of the event.
            Expired: await _dbContext.Set<GymSubscription>().AsNoTracking().CountAsync(
                subscription => subscription.Status == SubscriptionStatus.Expired
                    && subscription.CurrentPeriodEndUtc >= dayStart && subscription.CurrentPeriodEndUtc < dayEnd,
                cancellationToken));

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
