using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Analytics;

/// <summary>
/// The reads that the live dashboard (<c>DashboardQueries</c>) and the nightly compiler
/// (<see cref="DailyStatsCompiler"/>) must agree on. One definition each, so "living gym" and
/// "paying subscription" cannot drift apart between the number an operator sees now and the number
/// recorded for yesterday — the M5 whole-branch review found both duplicated verbatim.
/// </summary>
internal static class PlatformPopulationQueries
{
    /// <summary>Every gym that is not soft-deleted — the population every count is taken over.</summary>
    public static IQueryable<Gym> LivingGyms(AppDbContext dbContext) =>
        dbContext.Set<Gym>()
            .AsNoTracking()
            .Where(gym => gym.Status != GymStatus.Deleted);

    /// <summary>Living gyms grouped by status, one round trip. A status with no gyms is an absent key.</summary>
    public static async Task<IReadOnlyDictionary<GymStatus, int>> CountLivingGymsByStatusAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken) =>
        await LivingGyms(dbContext)
            .GroupBy(gym => gym.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, cancellationToken);

    /// <summary>
    /// One <see cref="MrrContribution"/> per subscription in a billing status — the input to
    /// <see cref="MonthlyRecurringRevenue.Calculate"/>. Materialised deliberately: MRR is domain
    /// arithmetic (AD-42), and the row count is the number of paying gyms, which is the platform's own
    /// size.
    /// </summary>
    public static async Task<IReadOnlyList<MrrContribution>> BillingContributionsAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => SubscriptionStatusTransitions.Billing.Contains(subscription.Status))
            .Select(subscription => new { subscription.Status, subscription.BillingCycle, subscription.Price })
            .ToListAsync(cancellationToken);

        return rows.Select(row => new MrrContribution(row.Status, row.BillingCycle, row.Price)).ToList();
    }
}
