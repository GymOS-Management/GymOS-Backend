using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes the feature-flag adapters (M4). Its own registration list, like gyms and subscriptions,
/// for the reason those are separate from each other: one list per surface keeps a future module
/// extraction a move rather than a rewrite.
/// </summary>
public static class PlatformFeaturesServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformFeatures(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IFeatureRepository, FeatureRepository>();
        services.TryAddScoped<IFeatureQueries, FeatureQueries>();

        return services;
    }
}
