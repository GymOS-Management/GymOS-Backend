using System.Data;
using System.Data.Common;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Persistence.Auditing;

/// <summary>
/// Keeps monthly partitions of <c>platform.audit_logs</c> created ahead of time
/// (ARCHITECTURE.md §2.6).
///
/// <para>The work itself lives in the <c>platform.ensure_audit_log_partitions</c> stored procedure
/// created by the <c>RuntimeDatabaseProgrammability</c> migration; this is the seam that calls it. Two
/// callers are intended and both use the same procedure, so partition boundaries cannot diverge
/// between them:</para>
/// <list type="bullet">
/// <item><b>Startup</b> — one cheap idempotent call per boot.</item>
/// <item><b>The recurring retention job</b> (AD-23) — which creates upcoming partitions and retires
/// expired ones in the same run, since those are two halves of one policy.</item>
/// </list>
///
/// <para><b>How partitioning differs from the PostgreSQL original, and why no audit row is ever
/// refused.</b> PostgreSQL declarative partitioning needs a partition to exist for a row's month or
/// the <c>INSERT</c> fails, which is why the original created a <c>DEFAULT</c> partition as a safety
/// net and why its maintenance function's failure mode was "rows land misfiled in DEFAULT". SQL Server
/// range partitioning has no such gap: a partition function with N boundaries always defines N+1
/// partitions covering the entire domain, so the trailing partition absorbs every future row whether
/// or not maintenance has run. There is nothing to create a DEFAULT for, and a write cannot be refused
/// for want of a partition. What this procedure does is <i>split</i> that trailing partition at each
/// new month boundary so the months stay separately retirable — falling behind costs granularity, not
/// correctness, and never costs a row.</para>
///
/// <para>Never throws: a partition boundary that cannot be created is a degraded state, not an outage.
/// Writes keep succeeding, so the platform stays correct — coarsely partitioned, but correct — and the
/// warning is what asks for attention. Failing startup here would take the whole API down over
/// housekeeping.</para>
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
        "Audit log partition maintenance failed. Writes continue into the trailing partition, so no " +
        "audit entry is lost, but the months it covers cannot be retired independently until the " +
        "boundaries exist. Investigate before the retention window elapses.")]
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
    /// Ensures a partition boundary exists for the current month and the configured lookahead.
    /// Idempotent — safe on every startup of every instance, and safe to run concurrently: the
    /// procedure checks the catalog and swallows the loser of a split race.
    /// </summary>
    public async Task EnsurePartitionsAsync(CancellationToken cancellationToken)
    {
        AuditLogPartitionOptions options = _options.Value;

        try
        {
            if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            {
                // The procedure itself is created by a migration, so calling it before migrations
                // have run would fail for an uninteresting reason.
                LogSkippedPendingMigrations(_logger);
                return;
            }

            // Read the clock at the edge and pass the month inward as data (AD-40).
            DateTime utcNow = _clock.UtcNow.UtcDateTime;
            var currentMonth = new DateOnly(utcNow.Year, utcNow.Month, 1);

            int created = await EnsureAsync(currentMonth, options.MonthsAhead, cancellationToken);

            LogPartitionsEnsured(_logger, options.MonthsAhead, currentMonth, created);
        }
        catch (SqlException exception)
        {
            LogMaintenanceFailed(_logger, exception);
        }
        catch (InvalidOperationException exception)
        {
            LogMaintenanceFailed(_logger, exception);
        }
    }

    /// <summary>
    /// Calls the procedure and returns how many boundaries it added.
    /// </summary>
    /// <remarks>
    /// Raw ADO.NET rather than <c>SqlQueryRaw</c>: <c>ALTER PARTITION FUNCTION … SPLIT RANGE</c> is DDL
    /// and has to live in a procedure, and a procedure's <c>OUTPUT</c> parameter is not something EF
    /// can compose a query over.
    /// </remarks>
    private async Task<int> EnsureAsync(DateOnly fromMonth, int monthsAhead, CancellationToken cancellationToken)
    {
        DbConnection connection = _dbContext.Database.GetDbConnection();
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using DbCommand command = connection.CreateCommand();

            command.Transaction = _dbContext.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "platform.ensure_audit_log_partitions";

            DbParameter fromMonthParameter = command.CreateParameter();
            fromMonthParameter.ParameterName = "@from_month";
            fromMonthParameter.DbType = DbType.Date;
            fromMonthParameter.Value = fromMonth.ToDateTime(TimeOnly.MinValue);
            command.Parameters.Add(fromMonthParameter);

            DbParameter monthsAheadParameter = command.CreateParameter();
            monthsAheadParameter.ParameterName = "@months_ahead";
            monthsAheadParameter.DbType = DbType.Int32;
            monthsAheadParameter.Value = monthsAhead;
            command.Parameters.Add(monthsAheadParameter);

            DbParameter createdParameter = command.CreateParameter();
            createdParameter.ParameterName = "@created";
            createdParameter.DbType = DbType.Int32;
            createdParameter.Direction = ParameterDirection.Output;
            command.Parameters.Add(createdParameter);

            await command.ExecuteNonQueryAsync(cancellationToken);

            return createdParameter.Value is int created ? created : 0;
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }
}
