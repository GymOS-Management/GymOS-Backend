using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Activation;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Application.DependencyInjection;

/// <summary>
/// Registers the platform module's use cases. Handlers are discovered by assembly
/// scan (AD-16) — adding a command never means editing a registration list.
/// </summary>
public static class PlatformApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHandlersFromAssembly(typeof(AssemblyMarker).Assembly);
        services.TryAddScoped<SessionFactory>();
        services.TryAddScoped<SuperAdminGuard>();
        services.TryAddScoped<InvoiceIssuer>();
        services.TryAddScoped<ActivationIssuer>();
        services.TryAddScoped<SubscriptionRenewalService>();
        services.TryAddScoped<InvoiceOverdueService>();

        return services;
    }
}
