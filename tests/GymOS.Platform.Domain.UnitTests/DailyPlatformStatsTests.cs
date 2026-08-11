using GymOS.Platform.Domain.Analytics;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class DailyPlatformStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Yesterday = new(2026, 8, 10);
    private static readonly GymCounts Counts = new(Total: 5, Active: 3, Suspended: 1, Provisioning: 1, New: 2, Canceled: 1, Expired: 0);

    [Fact]
    public void RecordKeepsTheCountsAndTheDate()
    {
        DailyPlatformStats stats = DailyPlatformStats.Record(Yesterday, Counts, [], Now);

        Assert.Equal(Yesterday, stats.StatDate);
        Assert.Equal(5, stats.TotalGyms);
        Assert.Equal(3, stats.ActiveGyms);
        Assert.Equal(1, stats.SuspendedGyms);
        Assert.Equal(1, stats.ProvisioningGyms);
        Assert.Equal(2, stats.NewGyms);
        Assert.Equal(1, stats.CanceledGyms);
        Assert.Equal(0, stats.ExpiredGyms);
        Assert.Empty(stats.Mrr);
    }

    [Fact]
    public void RecordCarriesOneMrrRowPerCurrency()
    {
        DailyPlatformStats stats = DailyPlatformStats.Record(
            Yesterday, Counts, [Money.Of(120m, Currency.Of("EUR")), Money.Of(80m, Currency.Of("GBP"))], Now);

        Assert.Collection(
            stats.Mrr,
            eur => Assert.Equal(Money.Of(120m, Currency.Of("EUR")), eur.Amount),
            gbp => Assert.Equal(Money.Of(80m, Currency.Of("GBP")), gbp.Amount));
    }

    [Fact]
    public void TheSameCurrencyTwiceIsADefect()
    {
        // The calculator groups by currency, so two rows for one currency means the caller summed
        // wrongly or built the list by hand. Refusing here stops a chart double-counting a day.
        Assert.Throws<DomainException>(() => DailyPlatformStats.Record(
            Yesterday, Counts, [Money.Of(1m, Currency.Of("EUR")), Money.Of(2m, Currency.Of("EUR"))], Now));
    }

    [Fact]
    public void CountsCannotBeNegative()
    {
        Assert.Throws<DomainException>(() => DailyPlatformStats.Record(
            Yesterday, Counts with { Total = -1 }, [], Now));
    }
}
