using System.Reflection;
using GymOS.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.BuildingBlocks.Application.DependencyInjection;

/// <summary>
/// Registration for the CQRS seam. Module manifests call
/// <see cref="AddHandlersFromAssembly"/> for their own assembly (AD-16);
/// the host composes the dispatcher and the pipeline once.
/// </summary>
public static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddCqrsDispatcher(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<DispatcherInvokerCache>();
        services.TryAddScoped<IDispatcher, Dispatcher>();
        return services;
    }

    /// <summary>
    /// Registers every concrete <c>ICommandHandler</c>/<c>IQueryHandler</c> in the
    /// assembly as scoped services, keyed by their closed handler interfaces.
    /// </summary>
    public static IServiceCollection AddHandlersFromAssembly(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            foreach (Type contract in type.GetInterfaces())
            {
                if (!contract.IsGenericType)
                {
                    continue;
                }

                Type definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(ICommandHandler<,>) || definition == typeof(IQueryHandler<,>))
                {
                    services.AddScoped(contract, type);
                }
            }
        }

        return services;
    }

    /// <summary>
    /// Registers an open-generic pipeline behavior. Composition follows registration
    /// order: the first registered behavior runs outermost (AD-39 pipeline order is
    /// therefore the registration order in the host).
    /// </summary>
    public static IServiceCollection AddPipelineBehavior(this IServiceCollection services, Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        bool implementsBehavior = openBehaviorType
            .GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>));

        if (!implementsBehavior)
        {
            throw new ArgumentException(
                $"{openBehaviorType.Name} does not implement IPipelineBehavior<,>.",
                nameof(openBehaviorType));
        }

        services.AddScoped(typeof(IPipelineBehavior<,>), openBehaviorType);
        return services;
    }
}
