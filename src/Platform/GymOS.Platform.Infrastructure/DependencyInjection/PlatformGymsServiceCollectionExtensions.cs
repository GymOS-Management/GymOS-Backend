using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes the gym-management adapters. Separate from <c>AddPlatformIdentity</c> for the same reason the
/// aggregates are separate: gyms and administrators are different surfaces with different lifecycles, and
/// one registration list per surface is what keeps a module extraction a move rather than a rewrite.
/// </summary>
public static class PlatformGymsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformGyms(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IGymRepository, GymRepository>();
        services.TryAddScoped<IGymQueries, GymQueries>();

        return services;
    }
}
