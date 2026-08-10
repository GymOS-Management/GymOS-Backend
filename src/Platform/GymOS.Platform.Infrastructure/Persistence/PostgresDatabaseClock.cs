using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>PostgreSQL's <c>now()</c> as <see cref="IDatabaseClock"/> — one round trip per ask.</summary>
internal sealed class PostgresDatabaseClock : IDatabaseClock
{
    private readonly AppDbContext _dbContext;

    public PostgresDatabaseClock(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<DateTimeOffset> UtcNowAsync(CancellationToken cancellationToken) =>
        _dbContext.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT now() AS \"Value\"")
            .SingleAsync(cancellationToken);
}
