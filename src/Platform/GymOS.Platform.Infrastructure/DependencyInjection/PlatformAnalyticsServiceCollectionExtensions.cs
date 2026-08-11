using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Infrastructure.Analytics;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>Composes the M5 dashboard and analytics adapters. One list per surface, like the others.</summary>
public static class PlatformAnalyticsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformAnalytics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPlatformStatsRepository, PlatformStatsRepository>();
        services.TryAddScoped<IDashboardQueries, DashboardQueries>();

        services.TryAddScoped<DailyStatsCompiler>();
        services.AddScheduledJob<PlatformStatsJob>();

        return services;
    }
}
