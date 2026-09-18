using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

/// <summary>
/// The monitoring page's live reads (M6 spec §4–5). Every query is O(jobs) or one aggregate over an
/// indexed column; nothing here is a read model. See <see cref="IMonitoringQueries"/>.
/// </summary>
internal sealed class MonitoringQueries : IMonitoringQueries
{
    private readonly AppDbContext _dbContext;

    public MonitoringQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken)
    {
        IQueryable<JobRun> runs = _dbContext.Set<JobRun>().AsNoTracking();

        // Newest run per job: group, take the max start, join back. Two grouped queries (this one
        // and lastSuccess below) plus one FirstAsync per job that has history — 2 + N round trips
        // over the same index, rather than a window function EF cannot express portably.
        var newest = await runs
            .GroupBy(run => run.JobName)
            .Select(group => new { JobName = group.Key, StartedAtUtc = group.Max(run => run.StartedAtUtc) })
            .ToListAsync(cancellationToken);

        var lastSuccess = await runs
            .Where(run => run.Status == JobRunStatus.Succeeded)
            .GroupBy(run => run.JobName)
            .Select(group => new { JobName = group.Key, StartedAtUtc = group.Max(run => run.StartedAtUtc) })
            .ToDictionaryAsync(row => row.JobName, row => row.StartedAtUtc, StringComparer.Ordinal, cancellationToken);

        var heads = new Dictionary<string, JobHistoryHead>(StringComparer.Ordinal);
        foreach (var head in newest)
        {
            JobRun latest = await runs
                .Where(run => run.JobName == head.JobName && run.StartedAtUtc == head.StartedAtUtc)
                .OrderByDescending(run => run.Id)
                .FirstAsync(cancellationToken);

            heads[head.JobName] = new JobHistoryHead(
                ToDto(latest),
                lastSuccess.TryGetValue(head.JobName, out DateTimeOffset succeededAt) ? succeededAt : null);
        }

        return heads;
    }

    public Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken) =>
        _dbContext.Set<JobRun>().AsNoTracking().AnyAsync(run => run.JobName == jobName, cancellationToken);

    public async Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<JobRun> query = _dbContext.Set<JobRun>().AsNoTracking()
            .Where(run => run.JobName == filter.JobName);

        if (filter.From is DateTimeOffset from)
        {
            query = query.Where(run => run.StartedAtUtc >= from);
        }

        if (filter.To is DateTimeOffset to)
        {
            query = query.Where(run => run.StartedAtUtc <= to);
        }

        if (filter.Status is JobRunStatus status)
        {
            query = query.Where(run => run.Status == status);
        }

        List<JobRun> runs = await query
            .OrderByDescending(run => run.StartedAtUtc)
            .ThenByDescending(run => run.Id)
            .Take(filter.Take)
            .ToListAsync(cancellationToken);

        return runs.Select(ToDto).ToList();
    }

    public async Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<OutboxMessage> messages = _dbContext.Set<OutboxMessage>().AsNoTracking();
        DateTimeOffset dayAgo = now.AddHours(-24);

        long total = await messages.LongCountAsync(cancellationToken);
        long last24h = await messages.LongCountAsync(message => message.CreatedAtUtc >= dayAgo, cancellationToken);
        DateTimeOffset? oldest = total == 0
            ? null
            : await messages.MinAsync(message => (DateTimeOffset?)message.CreatedAtUtc, cancellationToken);

        return new OutboxDepth(
            total,
            last24h,
            oldest is DateTimeOffset o ? (long)(now - o).TotalSeconds : null,
            OutboxDepth.PendingProcessorNote);
    }

    private static JobRunDto ToDto(JobRun run) => new(
        run.Id,
        run.JobName,
        run.Instance,
        run.Status.ToString(),
        run.StartedAtUtc,
        run.FinishedAtUtc,
        run.DurationMs,
        run.Error);
}
