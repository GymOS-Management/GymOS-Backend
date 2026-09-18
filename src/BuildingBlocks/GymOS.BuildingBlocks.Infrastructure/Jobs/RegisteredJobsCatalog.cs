using GymOS.BuildingBlocks.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>
/// Reads each registered job's name and schedule once, from a throwaway prototype in its own
/// scope — exactly what <see cref="JobHost.RunLoopAsync"/> does at start-up, for the same reason
/// (jobs are scoped; the catalog is a singleton). A job whose constructor throws is left out and
/// logged: the host already logs that job as dead, and a monitoring page that could not load
/// would hide every other job behind it.
/// </summary>
public sealed partial class RegisteredJobsCatalog : IRegisteredJobs
{
    private readonly Lazy<List<RegisteredJob>> _all;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Job {JobType} could not be described for monitoring and is omitted from the registered set.")]
    private static partial void LogOmitted(ILogger logger, string jobType, Exception exception);

    public RegisteredJobsCatalog(
        IEnumerable<ScheduledJobRegistration> registrations,
        IServiceScopeFactory scopeFactory,
        ILogger<RegisteredJobsCatalog> logger)
    {
        List<ScheduledJobRegistration> list = registrations.ToList();
        _all = new Lazy<List<RegisteredJob>>(() => Describe(list, scopeFactory, logger));
    }

    public IReadOnlyList<RegisteredJob> All => _all.Value;

    private static List<RegisteredJob> Describe(
        List<ScheduledJobRegistration> registrations, IServiceScopeFactory scopeFactory, ILogger logger)
    {
        var jobs = new List<RegisteredJob>(registrations.Count);
        using IServiceScope scope = scopeFactory.CreateScope();

        foreach (ScheduledJobRegistration registration in registrations)
        {
            try
            {
                var prototype = (IScheduledJob)scope.ServiceProvider.GetRequiredService(registration.JobType);
                jobs.Add(new RegisteredJob(prototype.Name, prototype.Schedule));
            }
            catch (Exception exception)
            {
                LogOmitted(logger, registration.JobType.Name, exception);
            }
        }

        return jobs.OrderBy(job => job.Name, StringComparer.Ordinal).ToList();
    }
}
