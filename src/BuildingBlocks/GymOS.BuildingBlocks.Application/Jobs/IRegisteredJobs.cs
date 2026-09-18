namespace GymOS.BuildingBlocks.Application.Jobs;

/// <summary>One job the host knows about: its stable name and when it runs.</summary>
public sealed record RegisteredJob(string Name, JobSchedule Schedule);

/// <summary>
/// The set of jobs registered with the host, for the monitoring page (M6): a job that has never
/// run still has a row there, because the registration exists even when the history does not.
/// </summary>
public interface IRegisteredJobs
{
    IReadOnlyList<RegisteredJob> All { get; }
}
