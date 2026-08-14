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
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var days = await _dbContext.Set<DailyPlatformStats>()
            .AsNoTracking()
            .Where(stats => stats.StatDate >= from && stats.StatDate <= to)
            .OrderBy(stats => stats.StatDate)

            // Only the days recorded. A missing day returned as zeros draws the chart to the floor and
            // reads as "we lost every gym that night"; a consumer that wants a continuous axis can see
            // the gap and decide for itself what to do with it.
            //
            // No .Include(stats => stats.Mrr) here: the Select below re-projects Mrr itself, and EF
            // ignores an Include beneath a projection (NavigationBaseIncludeIgnored, logged at
            // Warning on every call) rather than erroring, so the redundant Include only cost a log
            // line per request.
            .Select(stats => new
            {
                stats.StatDate,
                Mrr = stats.Mrr.Select(mrr => new MoneyDto(mrr.Amount.Amount, mrr.Amount.Currency.Code)).ToList(),
            })
            .ToListAsync(cancellationToken);

        // Each day's currencies are ordered here, in memory, and not in the query.
        //
        // They need ordering at all because the join that materialises the collection carries no
        // ORDER BY of its own, so two currencies on one day are a tie the database may break either
        // way — insertion order today, whatever a hash join or a table rewrite yields tomorrow.
        // `MonthlyRecurringRevenue.Calculate` sorts ordinally by code and `/dashboard/summary` passes
        // that through, so an unordered result here lets the two endpoints report the same platform's
        // currencies in opposite orders, and a client keyed on array position swaps its series.
        //
        // In memory rather than in SQL because `Currency` is a member of the `Money` complex type, and
        // ordering by it inside the collection projection does not translate — it fails at runtime, as
        // a 500, which is the same class of trap M3.1 hit when it tried to index inside a complex
        // property and had to write that index as raw SQL. The lists are one row per currency per day,
        // so sorting them here costs nothing.
        return days
            .Select(day => new RevenuePoint(
                day.StatDate,
                day.Mrr.OrderBy(money => money.Currency, StringComparer.Ordinal).ToList()))
            .ToList();
    }

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
