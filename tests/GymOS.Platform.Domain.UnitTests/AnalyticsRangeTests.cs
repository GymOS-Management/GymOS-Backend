using GymOS.Platform.Domain.Analytics.Rules;
using GymOS.SharedKernel.Results;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class AnalyticsRangeTests
{
    [Fact]
    public void AnOrderedRangeWithinTheCeilingIsAccepted()
    {
        Assert.True(AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 1)).IsSuccess);
    }

    [Fact]
    public void ABackwardsRangeIsRefused()
    {
        Result result = AnalyticsRange.Validate(new DateOnly(2026, 3, 1), new DateOnly(2026, 1, 1));

        Assert.True(result.IsFailure);
        Assert.Equal("analytics.range_invalid", result.Error!.Code);
    }

    [Fact]
    public void ARangeLongerThanAYearIsRefused()
    {
        // A stated ceiling is a refusal the caller can act on; an unbounded range is a page that
        // gets slower every year until someone notices.
        Result result = AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2027, 1, 3));

        Assert.True(result.IsFailure);
        Assert.Equal("analytics.range_invalid", result.Error!.Code);
    }

    [Fact]
    public void ExactlyTheCeilingIsAllowed()
    {
        Assert.True(AnalyticsRange.Validate(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(365)).IsSuccess);
    }
}
