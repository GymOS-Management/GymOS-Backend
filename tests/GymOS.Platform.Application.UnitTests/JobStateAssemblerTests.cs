using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

public sealed class JobStateAssemblerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    private static JobRunDto Run(string status, DateTimeOffset startedAt) =>
        new(Guid.NewGuid(), "renewal", "host", status, startedAt, status == "Running" ? null : startedAt.AddSeconds(3), 3000, null);

    [Fact]
    public void AJobThatHasNeverRunIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace, head: null));

    [Fact]
    public void AFreshSuccessIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Succeeded", Now.AddMinutes(-10)), Now.AddMinutes(-10))));

    [Fact]
    public void ASuccessOlderThanTwoPeriodsPlusGraceIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), Now.AddMinutes(-36))));

    [Fact]
    public void ASuccessJustInsideTwoPeriodsPlusGraceIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), Now.AddMinutes(-34))));

    [Fact]
    public void ARunStillRunningWithinPeriodPlusGraceIsNotStale() =>
        Assert.False(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Running", Now.AddMinutes(-19)), Now.AddMinutes(-30))));

    [Fact]
    public void ARunStillRunningPastPeriodPlusGraceIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Running", Now.AddMinutes(-21)), Now.AddMinutes(-30))));

    [Fact]
    public void AJobThatHasRunButNeverSucceededIsStale() =>
        Assert.True(JobStateAssembler.IsStale(Now, Period, Grace,
            new JobHistoryHead(Run("Failed", Now.AddMinutes(-1)), LastSucceededAtUtc: null)));

    [Fact]
    public void AssembleListsEveryRegisteredJobEvenWithoutHistory()
    {
        var jobs = new[]
        {
            new RegisteredJob("renewal", JobSchedule.Every(Period)),
            new RegisteredJob("retention", JobSchedule.DailyAtUtc(new TimeOnly(3, 10))),
        };
        var heads = new Dictionary<string, JobHistoryHead>
        {
            ["renewal"] = new(Run("Succeeded", Now.AddMinutes(-2)), Now.AddMinutes(-2)),
        };

        IReadOnlyList<JobStateDto> states = JobStateAssembler.Assemble(jobs, heads, Now, Grace);

        Assert.Equal(2, states.Count);
        JobStateDto renewal = states.Single(state => state.Name == "renewal");
        Assert.Equal("every 15m", renewal.Schedule);
        Assert.NotNull(renewal.LastRun);
        Assert.False(renewal.Stale);
        Assert.Equal(Now.AddMinutes(15), renewal.NextDueUtc);
        JobStateDto retention = states.Single(state => state.Name == "retention");
        Assert.Null(retention.LastRun);
        Assert.Null(retention.LastSucceededAtUtc);
        Assert.False(retention.Stale);
        Assert.Equal("daily at 03:10 UTC", retention.Schedule);
    }
}
