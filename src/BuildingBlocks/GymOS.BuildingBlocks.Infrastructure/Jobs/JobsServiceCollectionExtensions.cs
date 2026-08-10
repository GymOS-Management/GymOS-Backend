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
        services.AddSingleton(new ScheduledJobRegistration(typeof(TJob)));

        return services;
    }
}
