using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Idempotency;

/// <summary>
/// Startup deletion of expired idempotency records — the interim owner of AD-39 retention
/// until the M3.4 job runner lands (then this folds into the retention job, together with
/// AuditLogPartitionMaintainer; STATUS.md tracks that).
/// </summary>
public sealed partial class IdempotencyRecordMaintainer
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<IdempotencyRecordMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Idempotency record purge removed {RemovedCount} expired record(s).")]
    private static partial void LogPurged(ILogger logger, int removedCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Idempotency record purge skipped: the database has pending migrations.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Idempotency record purge failed; expired records remain until the next start.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    /// <summary>Creates the maintainer with its database context, clock, and logger.</summary>
    public IdempotencyRecordMaintainer(
        AppDbContext dbContext, IClock clock, ILogger<IdempotencyRecordMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Deletes idempotency records past their expiry. Idempotent and non-fatal: a failure here
    /// leaves stale rows in place rather than taking down startup over housekeeping.
    /// </summary>
    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                LogSkippedPendingMigrations(_logger);
                return;
            }

            DateTimeOffset now = _clock.UtcNow;
            int removed = await _dbContext.Database.ExecuteSqlAsync(
                $"DELETE FROM platform.idempotency_records WHERE expires_at < {now}",
                cancellationToken);

            LogPurged(_logger, removed);
        }
        catch (NpgsqlException exception) { LogPurgeFailed(_logger, exception); }
        catch (InvalidOperationException exception) { LogPurgeFailed(_logger, exception); }
    }
}
