using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace GymOS.Platform.Infrastructure.Persistence.Auditing;

/// <summary>
/// Keeps monthly partitions of <c>platform.audit_logs</c> created ahead of time
/// (ARCHITECTURE.md §2.6).
///
/// <para>The work itself lives in the <c>platform.ensure_audit_log_partitions</c> SQL function
/// created by the <c>PartitionAuditLogs</c> migration; this is the seam that calls it. Two
/// callers are intended and both use the same function, so partition naming and bounds cannot
/// diverge between them:</para>
/// <list type="bullet">
/// <item><b>Startup</b> (now) — the only scheduler that exists, and it costs one cheap
/// idempotent call per boot.</item>
/// <item><b>The recurring retention job</b> (AD-23, when the job runner lands) — which will
/// create upcoming partitions and detach expired ones in the same run, since those are two
/// halves of one policy.</item>
/// </list>
///
/// <para>Never throws: a partition that cannot be created is a degraded state, not an outage.
/// The DEFAULT partition keeps accepting writes, so the platform stays correct — misfiled, but
/// correct — and the warning is what asks for attention. Failing startup here would take the
/// whole API down over housekeeping.</para>
/// </summary>
public sealed partial class AuditLogPartitionMaintainer
{
    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<AuditLogPartitionOptions> _options;
    private readonly ILogger<AuditLogPartitionMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Audit log partitions ensured through {MonthsAhead} month(s) ahead of {FromMonth:yyyy-MM}: " +
        "{CreatedCount} created.")]
    private static partial void LogPartitionsEnsured(
        ILogger logger, int monthsAhead, DateOnly fromMonth, int createdCount);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Audit log partition maintenance skipped: the database has pending migrations.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Audit log partition maintenance failed. Writes continue into the DEFAULT partition, so " +
        "no audit entry is lost, but rows for new months will need moving before those months " +
        "can be partitioned or detached. Investigate before the retention window elapses.")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);

    public AuditLogPartitionMaintainer(
        AppDbContext dbContext,
        IClock clock,
        IOptions<AuditLogPartitionOptions> options,
        ILogger<AuditLogPartitionMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Ensures a partition exists for the current month and the configured lookahead.
    /// Idempotent — safe on every startup of every instance, and safe to run concurrently:
    /// the function checks the catalog and swallows the loser of a creation race.
    /// </summary>
    public async Task EnsurePartitionsAsync(CancellationToken cancellationToken)
    {
        AuditLogPartitionOptions options = _options.Value;

        try
        {
            if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                // The function itself is created by a migration, so calling it before migrations
                // have run would fail for an uninteresting reason.
                LogSkippedPendingMigrations(_logger);
                return;
            }

            // Read the clock at the edge and pass the month inward as data (AD-40).
            DateTime utcNow = _clock.UtcNow.UtcDateTime;
            var currentMonth = new DateOnly(utcNow.Year, utcNow.Month, 1);

            var fromMonth = new NpgsqlParameter<DateOnly>("from_month", currentMonth);
            var monthsAhead = new NpgsqlParameter<int>("months_ahead", options.MonthsAhead);

            int created = await _dbContext.Database
                .SqlQueryRaw<int>(
                    "SELECT platform.ensure_audit_log_partitions(@from_month, @months_ahead) AS \"Value\"",
                    fromMonth,
                    monthsAhead)
                .SingleAsync(cancellationToken);

            LogPartitionsEnsured(_logger, options.MonthsAhead, currentMonth, created);
        }
        catch (NpgsqlException exception)
        {
            LogMaintenanceFailed(_logger, exception);
        }
        catch (InvalidOperationException exception)
        {
            LogMaintenanceFailed(_logger, exception);
        }
    }
}
