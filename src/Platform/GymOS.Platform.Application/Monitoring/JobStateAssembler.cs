using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Monitoring;

namespace GymOS.Platform.Application.Monitoring;

/// <summary>
/// Composes the registered jobs with the heads of their histories (M6 spec §4). Pure: everything it
/// needs arrives as a parameter, so the stale rule is unit-tested against a fixed clock.
/// </summary>
public static class JobStateAssembler
{
    /// <summary>
    /// Stale when (a) the newest run is still Running past one period plus grace, or (b) there has been
    /// no success within two periods plus grace. A job with no history is not stale — it has had no
    /// chance yet; a job that has run and never succeeded is, because it has.
    /// </summary>
    public static bool IsStale(DateTimeOffset now, TimeSpan period, TimeSpan grace, JobHistoryHead? head)
    {
        if (head is null)
        {
            return false;
        }

        if (head.LatestRun.Status == nameof(JobRunStatus.Running)
            && now > head.LatestRun.StartedAtUtc + period + grace)
        {
            return true;
        }

        if (head.LastSucceededAtUtc is not DateTimeOffset lastSuccess)
        {
            return true;
        }

        return now > lastSuccess + period + period + grace;
    }

    public static IReadOnlyList<JobStateDto> Assemble(
        IReadOnlyList<RegisteredJob> jobs,
        IReadOnlyDictionary<string, JobHistoryHead> heads,
        DateTimeOffset now,
        TimeSpan grace)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(heads);

        return jobs
            .Select(job =>
            {
                heads.TryGetValue(job.Name, out JobHistoryHead? head);
                return new JobStateDto(
                    job.Name,
                    job.Schedule.Describe(),
                    head?.LatestRun,
                    head?.LastSucceededAtUtc,
                    job.Schedule.NextAfter(now),
                    IsStale(now, job.Schedule.Period, grace, head));
            })
            .ToList();
    }
}
