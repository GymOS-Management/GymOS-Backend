using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Infrastructure.Analytics;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Jobs;

/// <summary>
/// Records yesterday (M5). Nightly at 03:30 UTC, after <c>RetentionJob</c>'s 03:10.
///
/// <para>Yesterday rather than today because a day is only countable once it is over, and
/// <b>written once</b> because the counts are point-in-time: see <c>DailyPlatformStats</c>. A missed
/// night is a gap in the series and stays one — the alternative is a row invented from today's
/// state, which is worse than an honest hole in a chart.</para>
/// </summary>
internal sealed partial class PlatformStatsJob : IScheduledJob
{
    private readonly DailyStatsCompiler _compiler;
    private readonly IDatabaseClock _clock;
    private readonly ILogger<PlatformStatsJob> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message = "Platform stats recorded for {StatDate}.")]
    private static partial void LogRecorded(ILogger logger, DateOnly statDate);

    [LoggerMessage(Level = LogLevel.Debug, Message =
        "Platform stats for {StatDate} already exist; a recorded day is never rewritten.")]
    private static partial void LogAlreadyRecorded(ILogger logger, DateOnly statDate);

    public PlatformStatsJob(DailyStatsCompiler compiler, IDatabaseClock clock, ILogger<PlatformStatsJob> logger)
    {
        _compiler = compiler;
        _clock = clock;
        _logger = logger;
    }

    public string Name => "platform-stats";

    public JobSchedule Schedule => JobSchedule.DailyAtUtc(new TimeOnly(3, 30));

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The database's clock, not this host's: the row is read as history forever, and a host with
        // a fast clock would otherwise file a day under the wrong date (the M3.4 reasoning).
        DateTimeOffset databaseNow = await _clock.UtcNowAsync(cancellationToken);
        DateOnly yesterday = DateOnly.FromDateTime(databaseNow.UtcDateTime).AddDays(-1);

        if (await _compiler.CompileAsync(yesterday, databaseNow, cancellationToken))
        {
            LogRecorded(_logger, yesterday);
        }
        else
        {
            LogAlreadyRecorded(_logger, yesterday);
        }
    }
}
