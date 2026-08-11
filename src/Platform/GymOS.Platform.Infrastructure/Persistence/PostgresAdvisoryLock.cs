using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence;

/// <summary>
/// <see cref="IAdvisoryLock"/> as a PostgreSQL <b>transaction-scoped</b> advisory lock.
///
/// <para><c>pg_advisory_xact_lock</c> rather than the session-scoped variant the job lock uses:
/// this one is released by the transaction ending, whichever way it ends. A handler that throws
/// between acquiring and committing cannot leave the lock held — there is no dispose to forget,
/// and a crashed process releases it with its transaction.</para>
/// </summary>
internal sealed class PostgresAdvisoryLock : IAdvisoryLock
{
    private readonly AppDbContext _dbContext;

    public PostgresAdvisoryLock(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task AcquireAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // Namespaced so a set lock can never collide with the job locks in the same database.
        string key = $"gymos:set:{name}";

        await _dbContext.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({key}))",
            cancellationToken);
    }
}
