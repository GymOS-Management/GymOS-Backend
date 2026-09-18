using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Monitoring;

/// <summary>
/// The retention half of M6 (spec §3): job runs older than <see cref="MonitoringOptions.JobRunRetentionDays"/>
/// are deleted, <b>except the newest run of every job</b>, so a job that ran once in the spring still
/// shows its last outcome in the autumn rather than "never ran". Batched so a long-unswept table
/// never takes one lock; idempotent and non-fatal like the other maintainers.
/// </summary>
public sealed partial class JobRunMaintainer
{
    private const int BatchSize = 5000;

    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;
    private readonly ILogger<JobRunMaintainer> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Job run purge removed {RemovedCount} run(s) older than {RetentionDays} day(s).")]
    private static partial void LogPurged(ILogger logger, int removedCount, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Job run purge failed; old runs remain until the next sweep.")]
    private static partial void LogPurgeFailed(ILogger logger, Exception exception);

    public JobRunMaintainer(
        AppDbContext dbContext, IClock clock, IOptions<MonitoringOptions> options, ILogger<JobRunMaintainer> logger)
    {
        _dbContext = dbContext;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task PurgeAsync(CancellationToken cancellationToken)
    {
        int retentionDays = _options.Value.JobRunRetentionDays;
        DateTimeOffset cutoff = _clock.UtcNow.AddDays(-retentionDays);
        int removedTotal = 0;

        try
        {
            int removed;
            do
            {
                removed = await _dbContext.Database.ExecuteSqlAsync($"""
                    DELETE TOP ({BatchSize}) r
                    FROM platform.job_runs r
                    WHERE r.started_at_utc < {cutoff}
                      AND EXISTS (SELECT 1 FROM platform.job_runs newer
                                  WHERE newer.job_name = r.job_name
                                    AND newer.started_at_utc > r.started_at_utc)
                    """, cancellationToken);
                removedTotal += removed;
            }
            while (removed == BatchSize);

            LogPurged(_logger, removedTotal, retentionDays);
        }
        catch (SqlException exception) { LogPurgeFailed(_logger, exception); }
        catch (InvalidOperationException exception) { LogPurgeFailed(_logger, exception); }
    }
}
