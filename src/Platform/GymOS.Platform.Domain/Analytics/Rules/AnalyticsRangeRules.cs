using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Domain.Analytics.Rules;

/// <summary>
/// The bounds every analytics series accepts. Both ends are required and the span is capped, because
/// the table grows a row a day forever and a default range would eventually become a slow scan
/// nobody chose.
/// </summary>
public static class AnalyticsRange
{
    /// <summary>366 days — a full year including a leap day, inclusive of both ends.</summary>
    public const int MaximumDays = 366;

    public static Result Validate(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            return Result.Failure(Error.Validation(
                "analytics.range_invalid", "`to` must not be earlier than `from`."));
        }

        return to.DayNumber - from.DayNumber + 1 > MaximumDays
            ? Result.Failure(Error.Validation(
                "analytics.range_invalid",
                $"An analytics range covers at most {MaximumDays} days. Ask for a shorter period."))
            : Result.Success();
    }
}
