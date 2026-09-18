using GymOS.BuildingBlocks.Application.Jobs;

namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// A no-op <see cref="IJobRunRecorder"/> registered by <c>AddJobHost</c> so that a host which
/// composes the job runner without the monitoring module (<c>AddPlatformMonitoring</c>) still
/// runs its jobs — monitoring must never be why billing did not renew. The Platform module's
/// <c>AddPlatformMonitoring</c> replaces this registration with the real
/// <c>JobRunRecorder</c> when it is present.
/// </summary>
internal sealed class NullJobRunRecorder : IJobRunRecorder
{
    public Task<Guid> StartAsync(string jobName, string instance, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.Empty);

    public Task SucceedAsync(Guid runId, DateTimeOffset finishedAtUtc, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task FailAsync(Guid runId, DateTimeOffset finishedAtUtc, string error, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
