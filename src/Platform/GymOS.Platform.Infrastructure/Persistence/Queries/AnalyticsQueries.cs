using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

/// <summary>
/// The recorded series (§3.8), read only from <c>daily_platform_stats</c> — never from the
/// operational tables <c>DashboardQueries</c> reads live. See <see cref="IAnalyticsQueries"/>.
/// </summary>
internal sealed class AnalyticsQueries : IAnalyticsQueries
{
    private readonly AppDbContext _dbContext;

    public AnalyticsQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<RevenuePoint>> GetRevenueAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _dbContext.Set<DailyPlatformStats>()
            .AsNoTracking()
            .Include(stats => stats.Mrr)
            .Where(stats => stats.StatDate >= from && stats.StatDate <= to)
            .OrderBy(stats => stats.StatDate)

            // Only the days recorded. A missing day returned as zeros draws the chart to the floor and
            // reads as "we lost every gym that night"; a consumer that wants a continuous axis can see
            // the gap and decide for itself what to do with it.
            .Select(stats => new RevenuePoint(
                stats.StatDate,
                stats.Mrr.Select(mrr => new MoneyDto(mrr.Amount.Amount, mrr.Amount.Currency.Code)).ToList()))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<GrowthPoint>> GetGymsGrowthAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _dbContext.Set<DailyPlatformStats>()
            .AsNoTracking()
            .Where(stats => stats.StatDate >= from && stats.StatDate <= to)
            .OrderBy(stats => stats.StatDate)

            // Only the days recorded. A missing day returned as zeros draws the chart to the floor and
            // reads as "we lost every gym that night"; a consumer that wants a continuous axis can see
            // the gap and decide for itself what to do with it.
            .Select(stats => new GrowthPoint(
                stats.StatDate,
                stats.TotalGyms,
                stats.ActiveGyms,
                stats.SuspendedGyms,
                stats.ProvisioningGyms,
                stats.NewGyms))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ChurnPoint>> GetChurnAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
        await _dbContext.Set<DailyPlatformStats>()
            .AsNoTracking()
            .Where(stats => stats.StatDate >= from && stats.StatDate <= to)
            .OrderBy(stats => stats.StatDate)

            // Only the days recorded. A missing day returned as zeros draws the chart to the floor and
            // reads as "we lost every gym that night"; a consumer that wants a continuous axis can see
            // the gap and decide for itself what to do with it.
            .Select(stats => new ChurnPoint(
                stats.StatDate,
                stats.CanceledGyms,
                stats.ExpiredGyms,
                stats.CanceledGyms + stats.ExpiredGyms))
            .ToListAsync(cancellationToken);
}
