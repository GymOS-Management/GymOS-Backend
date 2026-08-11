using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Deletes refresh tokens nobody can use again — the purge <c>IRefreshTokenRepository</c> always
/// documented and nothing performed.
///
/// <para><b>Why it matters beyond disk.</b> Rotation inserts a row per refresh, so the table grows
/// with every 15-minute refresh of every session, forever. The paths that end sessions —
/// change-password, suspension, reuse-detection chain revocation — load and change-track *every*
/// token a user has ever held, so the suspension that is supposed to kill a compromised account
/// immediately becomes the slowest write in the system exactly when it matters most.</para>
///
/// <para>Kept rows: anything still usable, plus recently revoked/expired ones. The window exists
/// because a revoked token is evidence — reuse detection needs to recognize a replayed token to
/// know a leak happened, and deleting it the moment it is revoked turns "this was stolen" into
/// "unknown token".</para>
/// </summary>
internal sealed partial class RefreshTokenMaintainer
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<RefreshTokenMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Refresh-token purge removed {RemovedCount} token(s) unusable for more than {RetentionDays} day(s).")]
    private static partial void LogPurged(ILogger logger, int removedCount, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Refresh-token purge failed; the rows remain and the next run retries.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    public RefreshTokenMaintainer(
        AppDbContext dbContext, IClock clock, ILogger<RefreshTokenMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// How long an unusable token is kept for its evidence value. Thirty days comfortably outlives
    /// the refresh-token lifetime, so a replayed stolen token is still recognizable as reuse rather
    /// than as an unknown token.
    /// </summary>
    public const int RetentionDays = 30;

    public async Task PurgeUnusableAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset cutoff = _clock.UtcNow.AddDays(-RetentionDays);

            // Expired long ago, or revoked long ago. A live token is never touched, whatever its
            // age — its expiry is the only thing that ends it.
            int removed = await _dbContext.Database.ExecuteSqlAsync(
                $"""
                DELETE FROM platform.refresh_tokens
                WHERE expires_at_utc < {cutoff}
                   OR (revoked_at_utc IS NOT NULL AND revoked_at_utc < {cutoff})
                """,
                cancellationToken);

            LogPurged(_logger, removed, RetentionDays);
        }
        catch (NpgsqlException exception)
        {
            // Non-fatal, like the other maintenance halves: housekeeping must not take the job
            // (or, at startup, the host) down with it.
            LogPurgeFailed(_logger, exception);
        }
        catch (InvalidOperationException exception)
        {
            LogPurgeFailed(_logger, exception);
        }
    }
}
