using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Monitoring;

/// <summary>
/// Writes <c>platform.job_runs</c> for the job host (M6 spec §2). Start is an insert; the two finishes
/// are one <c>UPDATE … WHERE id = @id</c> each, issued without loading the row — nothing is tracked,
/// nothing is versioned, and a finished row is never touched again. Runs on the host's dedicated
/// recording scope, so this context is never the job's.
/// </summary>
internal sealed class JobRunRecorder : IJobRunRecorder
{
    private readonly AppDbContext _dbContext;

    public JobRunRecorder(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<Guid> StartAsync(
        string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken)
    {
        JobRun run = JobRun.Start(Guid.NewGuid(), jobName, instance, startedAtUtc);
        _dbContext.Set<JobRun>().Add(run);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _dbContext.Entry(run).State = EntityState.Detached;

        return run.Id;
    }

    public Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken) =>
        FinishAsync(runId, JobRunStatus.Succeeded, finishedAtUtc, error: null, cancellationToken);

    public Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken) =>
        FinishAsync(
            runId,
            JobRunStatus.Failed,
            finishedAtUtc,
            error.Length <= JobRun.MaxErrorLength ? error : error[..JobRun.MaxErrorLength],
            cancellationToken);

    private async Task FinishAsync(
        Guid runId, JobRunStatus status, DateTimeOffset finishedAtUtc, string? error, CancellationToken cancellationToken)
    {
        // duration_ms is computed in SQL from the row's own started_at_utc, so the two columns can
        // never disagree, whatever clock the caller read.
        await _dbContext.Set<JobRun>()
            .Where(run => run.Id == runId && run.Status == JobRunStatus.Running)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(run => run.Status, status)
                    .SetProperty(run => run.FinishedAtUtc, finishedAtUtc)
                    .SetProperty(run => run.DurationMs, run => (long)EF.Functions.DateDiffMillisecond(run.StartedAtUtc, finishedAtUtc))
                    .SetProperty(run => run.Error, error),
                cancellationToken);
    }
}
