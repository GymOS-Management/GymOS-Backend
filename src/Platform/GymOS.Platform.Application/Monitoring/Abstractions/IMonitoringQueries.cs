namespace GymOS.Platform.Application.Monitoring.Abstractions;

/// <summary>Live reads over <c>platform.job_runs</c> and <c>outbox.messages</c> (M6 spec §4–5). Nothing here is a read model.</summary>
public interface IMonitoringQueries
{
    /// <summary>For every job name that has rows: its newest run and its last success. Keyed by job name.</summary>
    Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken);

    Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken);

    /// <summary>Newest first, at most <see cref="JobRunFilter.Take"/> rows.</summary>
    Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken);

    Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken);
}
