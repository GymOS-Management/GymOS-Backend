using GymOS.Platform.Domain.Analytics;

namespace GymOS.Platform.Application.Analytics.Abstractions;

/// <summary>Write access to the daily read model. There is no update: rows are written once.</summary>
public interface IPlatformStatsRepository
{
    Task<bool> ExistsForAsync(DateOnly statDate, CancellationToken cancellationToken);

    void Add(DailyPlatformStats stats);
}
