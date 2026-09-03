using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding.DemoData;

/// <summary>
/// Derives one day of <c>daily_platform_stats</c> from the portfolio's own timeline.
///
/// <para><c>DailyStatsCompiler</c> is deliberately not used for history: its population reads are
/// as-of-now (the unwindowed-read caveat M5's review recorded), so asking it for a past day would
/// write today's population under yesterday's date, permanently — the rows are write-once. This
/// reads the same facts the compiler reads (living population by status, gyms created that day,
/// subscriptions canceled or expired that day, MRR from billing subscriptions) off the instants the
/// seeder is about to persist, so the history and the rows agree by construction.</para>
///
/// <para>Pure, so it is unit-tested without a database.</para>
/// </summary>
internal static class DemoTimeline
{
    public static (GymCounts Counts, IReadOnlyList<Money> Mrr) Compile(DemoPortfolio portfolio, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(portfolio);

        var dayStart = new DateTimeOffset(day, TimeOnly.MinValue, TimeSpan.Zero);
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        // The population is what stood at the end of the day, as the nightly job would have seen it.
        DateTimeOffset asOf = dayEnd.AddTicks(-1);

        int active = 0;
        int suspended = 0;
        int provisioning = 0;
        var contributions = new List<MrrContribution>();

        foreach (DemoGym gym in portfolio.Gyms)
        {
            switch (gym.PopulationStatusAt(asOf))
            {
                case GymStatus.Active:
                    active++;
                    break;
                case GymStatus.Suspended:
                    suspended++;
                    break;
                case GymStatus.Provisioning:
                    provisioning++;
                    break;
                default:
                    break;
            }

            // MRR is a subscription fact, not a gym fact: the compiler reads every billing
            // subscription regardless of its gym's status, and so does this.
            if (gym.Subscription is { } subscription
                && subscription.StatusAt(asOf) is { } status
                && SubscriptionStatusTransitions.IsBilling(status))
            {
                contributions.Add(new MrrContribution(
                    status, subscription.Cycle, portfolio.PriceOf(subscription.PlanCode, subscription.Cycle)));
            }
        }

        var counts = new GymCounts(
            Total: active + suspended + provisioning,
            Active: active,
            Suspended: suspended,
            Provisioning: provisioning,
            New: portfolio.Gyms.Count(gym => Within(gym.ProvisionedAt, dayStart, dayEnd)),
            Canceled: portfolio.Gyms.Count(gym => Within(gym.Subscription?.CanceledAt, dayStart, dayEnd)),
            Expired: portfolio.Gyms.Count(gym => Within(gym.Subscription?.ExpiredAt, dayStart, dayEnd)));

        return (counts, MonthlyRecurringRevenue.Calculate(contributions));
    }

    private static bool Within(DateTimeOffset? instant, DateTimeOffset start, DateTimeOffset end) =>
        instant is { } at && at >= start && at < end;
}
