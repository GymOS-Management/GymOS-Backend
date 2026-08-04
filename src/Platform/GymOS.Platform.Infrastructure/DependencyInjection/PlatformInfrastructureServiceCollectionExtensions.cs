using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes platform persistence. The host calls this once; connection string comes
/// from configuration (<c>ConnectionStrings:Database</c>) — environment/Key Vault in
/// production, appsettings.Development.json against docker-compose locally.
/// </summary>
public static class PlatformInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        string connectionString = configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException(
                "Connection string 'Database' is not configured. Set ConnectionStrings:Database " +
                "via appsettings, environment (ConnectionStrings__Database), or the secret store.");

        // Scoped: the interceptor reads the per-request ICorrelationContext. The
        // AddDbContext options callback receives the scoped provider, so resolution
        // below stays within the current request scope.
        services.TryAddScoped<OutboxSaveChangesInterceptor>();

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(provider.GetRequiredService<OutboxSaveChangesInterceptor>()));

        return services;
    }
}
