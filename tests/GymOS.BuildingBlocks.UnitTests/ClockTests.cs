using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests;

public sealed class ClockTests
{
    [Fact]
    public void FakeClock_MovesOnlyWhenTold()
    {
        var start = new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(start);

        Assert.Equal(start, clock.UtcNow);
        Assert.Equal(start, clock.UtcNow);

        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(start.AddMinutes(15), clock.UtcNow);

        var target = new DateTimeOffset(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);
        clock.Set(target);
        Assert.Equal(target, clock.UtcNow);
    }

    [Fact]
    public void SystemClock_ReportsUtcAndNeverGoesBackwards()
    {
        var clock = new SystemClock();

        DateTimeOffset first = clock.UtcNow;
        DateTimeOffset second = clock.UtcNow;

        Assert.Equal(TimeSpan.Zero, first.Offset);
        Assert.True(second >= first);
    }

    [Fact]
    public void AddBuildingBlocksInfrastructure_RegistersTheSystemClock()
    {
        var services = new ServiceCollection();
        services.AddBuildingBlocksInfrastructure();
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
    }
}
