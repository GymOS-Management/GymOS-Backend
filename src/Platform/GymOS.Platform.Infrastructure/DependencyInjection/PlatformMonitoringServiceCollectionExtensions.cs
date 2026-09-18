using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Infrastructure.Monitoring;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes M6 monitoring: the run recorder the job host writes through, the retention maintainer
/// the nightly retention job calls, and their shared options.
/// </summary>
public static class PlatformMonitoringServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformMonitoring(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // AddJobHost (BuildingBlocks) seeds a NullJobRunRecorder so a host without monitoring still
        // runs its jobs; Replace (not TryAdd) so registration order in Program.cs never decides
        // which recorder wins — this call always wins when it runs, regardless of call order.
        services.Replace(ServiceDescriptor.Scoped<IJobRunRecorder, JobRunRecorder>());
        services.TryAddScoped<JobRunMaintainer>();
        services.TryAddScoped<IMonitoringQueries, MonitoringQueries>();

        return services;
    }
}
