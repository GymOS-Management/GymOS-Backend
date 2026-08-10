using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Infrastructure.Invoices;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes the subscription-catalogue adapters. Separate from gyms and identity for the same reason
/// those are separate from each other: one registration list per surface is what keeps a future module
/// extraction a move rather than a rewrite.
/// </summary>
public static class PlatformSubscriptionsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSubscriptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddScoped<IPlanRepository, PlanRepository>();
        services.TryAddScoped<IPlanQueries, PlanQueries>();
        services.TryAddScoped<ISubscriptionRepository, SubscriptionRepository>();
        services.TryAddScoped<ISubscriptionQueries, SubscriptionQueries>();
        services.TryAddScoped<IInvoiceRepository, InvoiceRepository>();
        services.TryAddScoped<IInvoiceNumberGenerator, InvoiceNumberGenerator>();

        return services;
    }
}
