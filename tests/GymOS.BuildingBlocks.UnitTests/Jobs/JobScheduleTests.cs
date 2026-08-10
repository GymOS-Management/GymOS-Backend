using GymOS.BuildingBlocks.Application.Jobs;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

public sealed class JobScheduleTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnIntervalScheduleFiresOneIntervalLater() =>
        Assert.Equal(Noon.AddMinutes(15), JobSchedule.Every(TimeSpan.FromMinutes(15)).NextAfter(Noon));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveIntervalIsRefused(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => JobSchedule.Every(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void ADailyScheduleFiresLaterTheSameDayWhenTheTimeIsStillAhead()
    {
        JobSchedule schedule = JobSchedule.DailyAtUtc(new TimeOnly(15, 30));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 10, 15, 30, 0, TimeSpan.Zero),
            schedule.NextAfter(Noon));
    }

    [Fact]
    public void ADailyScheduleFiresTomorrowWhenTheTimeHasPassed()
    {
        JobSchedule schedule = JobSchedule.DailyAtUtc(new TimeOnly(3, 10));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 11, 3, 10, 0, TimeSpan.Zero),
            schedule.NextAfter(Noon));
    }

    [Fact]
    public void ADailyScheduleAskedAtExactlyItsOwnTimeWaitsForTomorrow()
    {
        // Strictly after: a job that just ran and asks again at its own due instant must get
        // tomorrow's slot, never an immediate second run.
        JobSchedule schedule = JobSchedule.DailyAtUtc(new TimeOnly(12, 0));

        Assert.Equal(Noon.AddDays(1), schedule.NextAfter(Noon));
    }

    [Fact]
    public void ADailyScheduleNormalizesNonUtcInput()
    {
        // The schedule is a UTC schedule whatever offset the caller's DateTimeOffset carries.
        JobSchedule schedule = JobSchedule.DailyAtUtc(new TimeOnly(15, 30));
        DateTimeOffset noonAsCyprusTime = Noon.ToOffset(TimeSpan.FromHours(3));

        Assert.Equal(
            new DateTimeOffset(2026, 8, 10, 15, 30, 0, TimeSpan.Zero),
            schedule.NextAfter(noonAsCyprusTime));
    }
}
