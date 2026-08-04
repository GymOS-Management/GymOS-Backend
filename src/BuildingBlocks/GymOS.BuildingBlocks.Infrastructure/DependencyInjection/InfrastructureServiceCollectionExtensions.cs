using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Context;
using GymOS.BuildingBlocks.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.BuildingBlocks.Infrastructure.DependencyInjection;

/// <summary>
/// Infrastructure implementations of the building-block seams. Grows with the
/// milestones (outbox, job runner, module loader); the host calls this once.
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBuildingBlocksInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddScoped<CorrelationContext>();
        services.TryAddScoped<ICorrelationContext>(provider => provider.GetRequiredService<CorrelationContext>());
        return services;
    }
}
