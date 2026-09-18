using GymOS.BuildingBlocks.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.BuildingBlocks.Infrastructure.Jobs;

/// <summary>Composes the job runner. Jobs register themselves via <see cref="AddScheduledJob{TJob}"/>.</summary>
public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddJobHost(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<JobHostOptions>().BindConfiguration(JobHostOptions.SectionName);
        services.AddHostedService<JobHost>();
        services.TryAddSingleton<IRegisteredJobs, RegisteredJobsCatalog>();

        // A null recorder so a host that composes the job runner without the monitoring module
        // still runs its jobs — monitoring must never be why billing did not renew. Platform's
        // AddPlatformMonitoring replaces this with the real recorder via services.Replace, so
        // registration order in Program.cs can never let this one win silently when both are called.
        services.TryAddScoped<IJobRunRecorder, NullJobRunRecorder>();

        return services;
    }

    /// <summary>
    /// Registers a job for the host. Scoped, like the handlers: a job's dependencies (DbContext,
    /// repositories) are scoped, and each tick runs in a fresh scope.
    /// </summary>
    public static IServiceCollection AddScheduledJob<TJob>(this IServiceCollection services)
        where TJob : class, IScheduledJob
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<TJob>();

        // Idempotent like every other registration in the composition: the module extensions are
        // all safe to call twice, and this must not be the one that quietly doubles a job's loop.
        bool alreadyRegistered = services.Any(descriptor =>
            descriptor.ServiceType == typeof(ScheduledJobRegistration)
            && descriptor.ImplementationInstance is ScheduledJobRegistration registration
            && registration.JobType == typeof(TJob));

        if (!alreadyRegistered)
        {
            services.AddSingleton(new ScheduledJobRegistration(typeof(TJob)));
        }

        return services;
    }
}
