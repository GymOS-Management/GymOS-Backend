using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// <see cref="IJobLock"/> as a PostgreSQL session advisory lock — the database every instance
/// already shares is the coordinator, the same reasoning as the outbox's SKIP LOCKED (AD-13).
///
/// <para>The lock is <b>session-scoped</b>: it lives exactly as long as the connection that took
/// it. Taking it explicitly opens the scope's <see cref="AppDbContext"/> connection and holds it
/// open, so the job's own queries in the same scope ride the same session and the lock cannot
/// outlive — or die before — the run it protects. Disposing the handle unlocks and closes; a
/// crashed process drops its connection and PostgreSQL releases the lock by itself, so a dead
/// instance can never wedge a job shut.</para>
/// </summary>
internal sealed class PostgresJobLock : IJobLock
{
    private readonly AppDbContext _dbContext;

    public PostgresJobLock(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IAsyncDisposable?> TryAcquireAsync(string jobName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        // Held open for the run — see the type's remarks. Closed by the handle, or by scope
        // disposal if acquisition fails mid-way.
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        var key = new NpgsqlParameter<string>("p_key", LockKey(jobName));
        bool acquired = await _dbContext.Database
            .SqlQueryRaw<bool>("SELECT pg_try_advisory_lock(hashtext(@p_key)) AS \"Value\"", key)
            .SingleAsync(cancellationToken);

        if (!acquired)
        {
            await _dbContext.Database.CloseConnectionAsync();
            return null;
        }

        return new Handle(_dbContext, jobName);
    }

    /// <summary>
    /// Namespaced so a job lock can never collide with any other advisory-lock use of the same
    /// database. <c>hashtext</c> maps it into the bigint key space on the PostgreSQL side.
    /// </summary>
    private static string LockKey(string jobName) => $"gymos:job:{jobName}";

    private sealed class Handle : IAsyncDisposable
    {
        private readonly AppDbContext _dbContext;
        private readonly string _jobName;

        public Handle(AppDbContext dbContext, string jobName)
        {
            _dbContext = dbContext;
            _jobName = jobName;
        }

        public async ValueTask DisposeAsync()
        {
            var key = new NpgsqlParameter<string>("p_key", LockKey(_jobName));

            // Explicit unlock rather than relying on connection close alone: the scope's context
            // may pool the physical connection, and a pooled session keeps its advisory locks.
            await _dbContext.Database
                .SqlQueryRaw<bool>("SELECT pg_advisory_unlock(hashtext(@p_key)) AS \"Value\"", key)
                .SingleAsync();

            await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
