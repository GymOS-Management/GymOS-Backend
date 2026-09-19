using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Analytics;
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
        // One round trip for the four population counts rather than four — and the same definition of
        // "living" the nightly compiler uses, from the same place.
        IReadOnlyDictionary<GymStatus, int> population =
            await PlatformPopulationQueries.CountLivingGymsByStatusAsync(_dbContext, cancellationToken);
        int Living(GymStatus status) => population.TryGetValue(status, out int count) ? count : 0;

        int liveSubscriptions = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .CountAsync(subscription => SubscriptionStatusTransitions.Live.Contains(subscription.Status), cancellationToken);

        IReadOnlyList<Money> mrr = MonthlyRecurringRevenue.Calculate(
            await PlatformPopulationQueries.BillingContributionsAsync(_dbContext, cancellationToken));

        return new DashboardSummary(
            population.Values.Sum(),
            Living(GymStatus.Active),
            Living(GymStatus.Suspended),
            Living(GymStatus.Provisioning),
            liveSubscriptions,

            // Already in ordinal currency order: MonthlyRecurringRevenue.Calculate groups and sorts by
            // code, so this and /analytics/revenue agree without a second sort here.
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

                // Null for explicit audit entries (a failed sign-in, for instance) — the same shape
                // AuditLogQueries projects, so the feed and the log agree.
                log.EntityType,
                log.UserEmail))
            .ToListAsync(cancellationToken);
}
