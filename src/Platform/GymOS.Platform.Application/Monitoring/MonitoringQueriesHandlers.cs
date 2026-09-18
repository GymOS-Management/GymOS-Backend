using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Monitoring;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>Health, job state, outbox depth and this replica's process, in one document (M6 spec §4).</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record GetMonitoringOverviewQuery : IQuery<Result<MonitoringOverview>>;

/// <summary>The overview's jobs block on its own, for a page that polls job state.</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record GetJobStatesQuery : IQuery<Result<IReadOnlyList<JobStateDto>>>;

/// <summary>One job's run history, newest first, paged by time (M6 spec §5).</summary>
[RequiresPermission(PlatformPermissions.MonitoringRead)]
public sealed record ListJobRunsQuery(
    string JobName, DateTimeOffset? From, DateTimeOffset? To, string? Status, int Limit) : IQuery<Result<JobRunsPage>>;

internal sealed class GetMonitoringOverviewQueryHandler : IQueryHandler<GetMonitoringOverviewQuery, Result<MonitoringOverview>>
{
    private readonly IDashboardHealthCheck _healthCheck;
    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;
    private readonly IProcessMetrics _processMetrics;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;

    public GetMonitoringOverviewQueryHandler(
        IDashboardHealthCheck healthCheck,
        IMonitoringQueries queries,
        IRegisteredJobs registeredJobs,
        IProcessMetrics processMetrics,
        IClock clock,
        IOptions<MonitoringOptions> options)
    {
        _healthCheck = healthCheck;
        _queries = queries;
        _registeredJobs = registeredJobs;
        _processMetrics = processMetrics;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<MonitoringOverview>> HandleAsync(GetMonitoringOverviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset now = _clock.UtcNow;
        DashboardHealth health = await _healthCheck.CheckAsync(cancellationToken);
        IReadOnlyDictionary<string, JobHistoryHead> heads = await _queries.GetJobHistoryHeadsAsync(cancellationToken);
        OutboxDepth outbox = await _queries.GetOutboxDepthAsync(now, cancellationToken);

        return Result.Success(new MonitoringOverview(
            health,
            JobStateAssembler.Assemble(_registeredJobs.All, heads, now, _options.Value.StaleGrace),
            outbox,
            _processMetrics.Current));
    }
}

internal sealed class GetJobStatesQueryHandler : IQueryHandler<GetJobStatesQuery, Result<IReadOnlyList<JobStateDto>>>
{
    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;
    private readonly IClock _clock;
    private readonly IOptions<MonitoringOptions> _options;

    public GetJobStatesQueryHandler(
        IMonitoringQueries queries, IRegisteredJobs registeredJobs, IClock clock, IOptions<MonitoringOptions> options)
    {
        _queries = queries;
        _registeredJobs = registeredJobs;
        _clock = clock;
        _options = options;
    }

    public async Task<Result<IReadOnlyList<JobStateDto>>> HandleAsync(GetJobStatesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        DateTimeOffset now = _clock.UtcNow;
        IReadOnlyDictionary<string, JobHistoryHead> heads = await _queries.GetJobHistoryHeadsAsync(cancellationToken);

        return Result.Success(JobStateAssembler.Assemble(_registeredJobs.All, heads, now, _options.Value.StaleGrace));
    }
}

internal sealed class ListJobRunsQueryHandler : IQueryHandler<ListJobRunsQuery, Result<JobRunsPage>>
{
    public const int MaxLimit = 200;
    public const int DefaultLimit = 50;

    private readonly IMonitoringQueries _queries;
    private readonly IRegisteredJobs _registeredJobs;

    public ListJobRunsQueryHandler(IMonitoringQueries queries, IRegisteredJobs registeredJobs)
    {
        _queries = queries;
        _registeredJobs = registeredJobs;
    }

    public async Task<Result<JobRunsPage>> HandleAsync(ListJobRunsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.From is DateTimeOffset from && query.To is DateTimeOffset to && from > to)
        {
            return Result.Failure<JobRunsPage>(Error.Validation("monitoring.invalid_range", "`from` must not be after `to`."));
        }

        JobRunStatus? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (!Enum.TryParse(query.Status, ignoreCase: true, out JobRunStatus parsed) || !Enum.IsDefined(parsed))
            {
                return Result.Failure<JobRunsPage>(Error.Validation(
                    "monitoring.invalid_status", "`status` must be one of Running, Succeeded, Failed."));
            }

            status = parsed;
        }

        // A registered job is always addressable, even before its first run; a renamed or removed job
        // stays addressable while its rows last, so history is never orphaned behind a 404.
        bool known = _registeredJobs.All.Any(job => string.Equals(job.Name, query.JobName, StringComparison.Ordinal))
            || await _queries.HasRunsAsync(query.JobName, cancellationToken);
        if (!known)
        {
            return Result.Failure<JobRunsPage>(Error.NotFound(
                "monitoring.job_not_found", $"No job named '{query.JobName}' is registered or has run history."));
        }

        int limit = query.Limit <= 0 ? DefaultLimit : Math.Min(query.Limit, MaxLimit);

        // One more than asked: the presence of that row is `hasMore`, without a second COUNT query.
        IReadOnlyList<JobRunDto> runs = await _queries.ListRunsAsync(
            new JobRunFilter(query.JobName, query.From, query.To, status, limit + 1), cancellationToken);

        bool hasMore = runs.Count > limit;

        return Result.Success(new JobRunsPage(hasMore ? runs.Take(limit).ToList() : runs, hasMore));
    }
}
