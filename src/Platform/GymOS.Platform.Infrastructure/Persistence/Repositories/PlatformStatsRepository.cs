using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Analytics;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

internal sealed class PlatformStatsRepository : IPlatformStatsRepository
{
    private readonly AppDbContext _dbContext;

    public PlatformStatsRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<bool> ExistsForAsync(DateOnly statDate, CancellationToken cancellationToken) =>
        _dbContext.Set<DailyPlatformStats>().AnyAsync(stats => stats.StatDate == statDate, cancellationToken);

    public void Add(DailyPlatformStats stats) => _dbContext.Set<DailyPlatformStats>().Add(stats);
}
