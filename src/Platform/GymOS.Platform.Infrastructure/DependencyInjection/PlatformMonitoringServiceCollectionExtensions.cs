using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>Composes M6 monitoring: the run recorder the job host writes through, and its options.</summary>
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

        services.TryAddScoped<IJobRunRecorder, JobRunRecorder>();

        return services;
    }
}
