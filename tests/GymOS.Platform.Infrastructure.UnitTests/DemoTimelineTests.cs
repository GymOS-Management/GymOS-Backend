using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Infrastructure.Persistence.Seeding.DemoData;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// The pure half of the demo seeder: ninety days of platform stats derived from the portfolio's
/// timeline. What matters is that every row reconciles, that the series ends where the live
/// population stands, and that the churn inside the window lands on its days.
/// </summary>
public sealed class DemoTimelineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 9, 3);

    [Fact]
    public void EveryDayReconciles_AndCarriesOneEuroMrr()
    {
        DemoPortfolio portfolio = DemoPortfolio.Build(Now);

        for (int daysBack = DemoDataSeeder.HistoryDays; daysBack >= 1; daysBack--)
        {
            (GymCounts counts, IReadOnlyList<Money> mrr) = DemoTimeline.Compile(portfolio, Today.AddDays(-daysBack));

            Assert.Equal(counts.Total, counts.Active + counts.Suspended + counts.Provisioning);
            Money euro = Assert.Single(mrr);
            Assert.Equal("EUR", euro.Currency.Code);
            Assert.True(euro.Amount > 0, $"MRR must be positive {daysBack} days back");
        }
    }

    [Fact]
    public void Yesterday_IsTheLivingPopulationTheSeederWrites()
    {
        DemoPortfolio portfolio = DemoPortfolio.Build(Now);

        (GymCounts counts, IReadOnlyList<Money> mrr) = DemoTimeline.Compile(portfolio, Today.AddDays(-1));

        Assert.Equal(12, counts.Total);
        Assert.Equal(10, counts.Active);
        Assert.Equal(1, counts.Suspended);
        Assert.Equal(1, counts.Provisioning);

        // Eight billing subscriptions: seven Active and one PastDue. The trial and the terminal ones
        // contribute nothing, and the deleted gym's canceled subscription contributes nothing.
        // Monthly 99 + 49 + 49 + 249 + 99, quarterly 279, yearly 2490 and 490, normalised to a month.
        Money euro = Assert.Single(mrr);
        Assert.InRange(euro.Amount, 880m, 895m);
    }

    [Fact]
    public void ChurnInsideTheWindow_LandsOnItsDays()
    {
        DemoPortfolio portfolio = DemoPortfolio.Build(Now);

        var series = Enumerable.Range(1, DemoDataSeeder.HistoryDays)
            .Select(daysBack => DemoTimeline.Compile(portfolio, Today.AddDays(-daysBack)).Counts)
            .ToList();

        Assert.Equal(2, series.Sum(counts => counts.Canceled));
        Assert.Equal(1, series.Sum(counts => counts.Expired));

        // Provisioned inside the window: Coastline (90 d — on the edge, counted), Titan, Flow,
        // CrossBox, Harbour, Eastside.
        Assert.Equal(6, series.Sum(counts => counts.New));
    }

    [Fact]
    public void ADeletedGym_LeavesThePopulation_ButItsCancellationStaysInTheSeries()
    {
        DemoPortfolio portfolio = DemoPortfolio.Build(Now);
        DemoGym deleted = Assert.Single(portfolio.Gyms, gym => gym.DeletedAt is not null);
        DateOnly deletionDay = DateOnly.FromDateTime(deleted.DeletedAt!.Value.UtcDateTime);

        (GymCounts before, _) = DemoTimeline.Compile(portfolio, deletionDay.AddDays(-1));
        (GymCounts onTheDay, _) = DemoTimeline.Compile(portfolio, deletionDay);

        Assert.Equal(before.Total - 1, onTheDay.Total);
        Assert.Equal(1, onTheDay.Canceled);
    }

    [Fact]
    public void ThePortfolioIsAlwaysRecent_WhateverTheClockSays()
    {
        // Built at one minute past midnight: every instant must still be strictly in the past.
        var justAfterMidnight = new DateTimeOffset(2026, 9, 3, 0, 1, 0, TimeSpan.Zero);
        DemoPortfolio portfolio = DemoPortfolio.Build(justAfterMidnight);

        foreach (DemoGym gym in portfolio.Gyms)
        {
            Assert.True(gym.ProvisionedAt < justAfterMidnight, gym.Slug);
            Assert.True(gym.LastChangedAt < justAfterMidnight, gym.Slug);

            if (gym.Subscription is { } subscription)
            {
                Assert.True(subscription.AssignedAt < justAfterMidnight, gym.Slug);
            }
        }
    }
}
