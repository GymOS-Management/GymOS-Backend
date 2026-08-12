using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Deletes activation tokens nobody can use again (ADR-53), alongside the refresh-token purge on
/// <c>RetentionJob</c>'s nightly pass.
///
/// <para>Every invitation and every reset adds a row, and every re-send adds another while spending
/// the one before it — so the table accumulates spent credentials indefinitely. A credential table
/// that only grows is precisely the defect <see cref="RefreshTokenMaintainer"/> was added to prevent;
/// this is the same reasoning applied to the same shape of data.</para>
///
/// <para>The retention window is shorter than the refresh tokens' because these rows carry no
/// evidential value: an activation token has no rotation chain and no reuse to detect. What is kept
/// is a short tail, so an operator investigating "did that invitation ever go out?" the next morning
/// still finds the row. The audit trail answers it permanently either way.</para>
/// </summary>
internal sealed partial class ActivationTokenMaintainer
{
    /// <summary>Days after expiry that a spent or dead token is kept.</summary>
    public const int RetentionDays = 7;

    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly ILogger<ActivationTokenMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Activation-token purge removed {RemovedCount} token(s) expired more than {RetentionDays} day(s) ago.")]
    private static partial void LogPurged(ILogger logger, int removedCount, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Activation-token purge failed; the rows remain and the next run retries.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    public ActivationTokenMaintainer(
        AppDbContext dbContext, IClock clock, ILogger<ActivationTokenMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _logger = logger;
    }

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        try
        {
            DateTimeOffset cutoff = _clock.UtcNow.AddDays(-RetentionDays);

            // Keyed on expiry alone. A consumed token still has an expiry, and a token that is still
            // live is never touched however long ago it was issued — the link in somebody's inbox
            // must keep working for exactly as long as it promised.
            int removed = await _dbContext.Database.ExecuteSqlAsync(
                $"DELETE FROM platform.user_activation_tokens WHERE expires_at_utc < {cutoff}",
                cancellationToken);

            LogPurged(_logger, removed, RetentionDays);
        }
        catch (NpgsqlException exception)
        {
            // Non-fatal, like every other maintenance half: housekeeping must not take the job down.
            LogPurgeFailed(_logger, exception);
        }
        catch (InvalidOperationException exception)
        {
            LogPurgeFailed(_logger, exception);
        }
    }
}
