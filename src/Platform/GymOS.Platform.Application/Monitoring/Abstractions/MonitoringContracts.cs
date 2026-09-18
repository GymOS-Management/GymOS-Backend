using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Monitoring;

namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>One recorded run. <c>Status</c> is the enum name, <c>Error</c> is type and message only.</summary>
public sealed record JobRunDto(
    Guid Id,
    string JobName,
    string Instance,
    string Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    long? DurationMs,
    string? Error);

/// <summary>The head of one job's history: its newest run, and when it last succeeded (any run).</summary>
public sealed record JobHistoryHead(JobRunDto LatestRun, DateTimeOffset? LastSucceededAtUtc);

/// <summary>One registered job as the console shows it. <c>Stale</c> is computed on read; nothing stores it.</summary>
public sealed record JobStateDto(
    string Name,
    string Schedule,
    JobRunDto? LastRun,
    DateTimeOffset? LastSucceededAtUtc,
    DateTimeOffset NextDueUtc,
    bool Stale);

/// <summary>
/// Depth only (M6 spec §1, §4): there is no outbox processor and no delivery row yet, so "lag" would be
/// the age of the oldest message ever written. <see cref="Note"/> says so in the payload.
/// </summary>
public sealed record OutboxDepth(long TotalMessages, long MessagesLast24h, long? OldestMessageAgeSeconds, string Note)
{
    public const string PendingProcessorNote =
        "Depth only. Delivery lag and dead letters are reported once the outbox processor exists.";
}

/// <summary>One replica's view of its own process. <c>CpuPercent</c> is null until two samples exist.</summary>
public sealed record ProcessMetrics(
    double? CpuPercent, long WorkingSetMb, long UptimeSeconds, string Instance, DateTimeOffset SampledAtUtc);

public sealed record MonitoringOverview(
    DashboardHealth Health, IReadOnlyList<JobStateDto> Jobs, OutboxDepth Outbox, ProcessMetrics? Process);

/// <summary>Newest first. <c>HasMore</c> means older runs exist before the last one returned.</summary>
public sealed record JobRunsPage(IReadOnlyList<JobRunDto> Runs, bool HasMore);

/// <summary>The history page's filter. <c>Take</c> is already clamped by the handler.</summary>
public sealed record JobRunFilter(string JobName, DateTimeOffset? From, DateTimeOffset? To, JobRunStatus? Status, int Take);
