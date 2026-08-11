using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

/// <summary>
/// The dashboard's live reads (§3.2). Every query here runs against the operational tables at
/// request time — the same tables <c>DailyStatsCompiler</c> reads to write AD-8's read model, and
/// deliberately not that read model itself. See <see cref="IDashboardQueries"/>.
/// </summary>
internal sealed class DashboardQueries : IDashboardQueries
{
    private readonly AppDbContext _dbContext;

    public DashboardQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<DashboardSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        IQueryable<Gym> livingGyms = _dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Status != GymStatus.Deleted);

        int totalGyms = await livingGyms.CountAsync(cancellationToken);
        int activeGyms = await livingGyms.CountAsync(gym => gym.Status == GymStatus.Active, cancellationToken);
        int suspendedGyms = await livingGyms.CountAsync(gym => gym.Status == GymStatus.Suspended, cancellationToken);
        int provisioningGyms = await livingGyms.CountAsync(
            gym => gym.Status == GymStatus.Provisioning, cancellationToken);

        int liveSubscriptions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .CountAsync(subscription => SubscriptionStatusTransitions.Live.Contains(subscription.Status), cancellationToken);

        // Same billing set, same calculator DailyStatsCompiler uses for the nightly row — one
        // definition of the platform's headline number, computed live instead of read back.
        var contributions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => SubscriptionStatusTransitions.Billing.Contains(subscription.Status))
            .Select(subscription => new { subscription.Status, subscription.BillingCycle, subscription.Price })
            .ToListAsync(cancellationToken);

        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            contributions.Select(row => new MrrContribution(row.Status, row.BillingCycle, row.Price)));

        return new DashboardSummary(
            totalGyms,
            activeGyms,
            suspendedGyms,
            provisioningGyms,
            liveSubscriptions,
            mrr.Select(amount => new MoneyDto(amount.Amount, amount.Currency.Code)).ToList());
    }

    public async Task<IReadOnlyList<ActivityEntry>> GetRecentActivityAsync(
        int limit, CancellationToken cancellationToken) =>
        await _dbContext.Set<AuditLog>()
            .AsNoTracking()
            .OrderByDescending(log => log.CreatedAtUtc)
            .ThenBy(log => log.Id)
            .Take(limit)
            .Select(log => new ActivityEntry(
                log.CreatedAtUtc,
                log.Action,

                // Explicit audit entries (a failed sign-in, for instance) carry no entity type. The
                // feed reports that as empty rather than widening the wire contract to nullable.
                log.EntityType ?? string.Empty,
                log.UserEmail))
            .ToListAsync(cancellationToken);
}
