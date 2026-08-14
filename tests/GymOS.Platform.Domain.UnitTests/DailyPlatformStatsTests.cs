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

    [Fact]
    public void RecordRefusesAPopulationThatDoesNotReconcile()
    {
        // Analytics.md promises this identity explicitly: "without a column for it active + suspended
        // would never reconcile to total — which reads as a bug to anyone who checks the arithmetic."
        // GymStatus has exactly four values and the living-gym base excludes Deleted, so the three
        // status counts partition the total exactly. There is no legitimate way to produce a row
        // where they do not.
        //
        // The way an illegitimate one arose: the compiler issued four independent COUNT queries, each
        // with its own READ COMMITTED snapshot, so a gym created between them was counted by some and
        // not others. The row was then stored — write-once, never recomputed — and
        // /analytics/gyms-growth served that self-contradicting day forever. This guard makes the
        // aggregate refuse it rather than persist it.
        // The real interleaving: the total was read first and returned 100, then a gym was created,
        // then the three status counts returned 60/10/31 — summing to 101 against a total of 100.
        var inconsistent = new GymCounts(
            Total: 100, Active: 60, Suspended: 10, Provisioning: 31, New: 0, Canceled: 0, Expired: 0);

        DomainException error = Assert.Throws<DomainException>(
            () => DailyPlatformStats.Record(Yesterday, inconsistent, [], Now));

        Assert.Contains("reconcile", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordAcceptsAPopulationThatReconciles()
    {
        // The other direction, so the guard cannot be satisfied by refusing everything.
        var consistent = new GymCounts(
            Total: 100, Active: 60, Suspended: 10, Provisioning: 30, New: 4, Canceled: 2, Expired: 1);

        DailyPlatformStats stats = DailyPlatformStats.Record(Yesterday, consistent, [], Now);

        Assert.Equal(100, stats.TotalGyms);
    }

}
