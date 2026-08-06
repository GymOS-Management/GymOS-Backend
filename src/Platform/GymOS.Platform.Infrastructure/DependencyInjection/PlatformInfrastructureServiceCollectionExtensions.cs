using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
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

        // Scoped: the outbox interceptor reads the per-request ICorrelationContext.
        // The AddDbContext options callback receives the scoped provider, so
        // resolution below stays within the current request scope.
        services.TryAddScoped<OutboxSaveChangesInterceptor>();
        services.TryAddScoped<AuditableTimestampsInterceptor>();
        services.TryAddScoped<AuditSaveChangesInterceptor>();
        services.TryAddScoped<PlatformIdentitySeeder>();

        // The audited-entity declaration never changes at runtime, so the policy is a
        // singleton built once from the module's list (AD-7).
        services.TryAddSingleton(new AuditPolicy(PlatformAuditedEntities.All));

        services.AddOptions<AuditLogPartitionOptions>()
            .Bind(configuration.GetSection(AuditLogPartitionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<AuditLogPartitionMaintainer>();
        services.TryAddScoped<IIdempotencyStore, IdempotencyStore>();

        services.AddOptions<IdempotencyOptions>()
            .Bind(configuration.GetSection(IdempotencyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddScoped<IdempotencyRecordMaintainer>();

        services.TryAddScoped<IAuditTrail>(provider => new AuditTrail(
            provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ICurrentUser>(),
            provider.GetRequiredService<ICorrelationContext>()));

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!))
            .UseSnakeCaseNamingConvention()
            // Order matters: timestamps are stamped before the audit interceptor reads
            // values, so created_at/updated_at appear in the captured diff.
            .AddInterceptors(
                provider.GetRequiredService<AuditableTimestampsInterceptor>(),
                provider.GetRequiredService<AuditSaveChangesInterceptor>(),
                provider.GetRequiredService<OutboxSaveChangesInterceptor>()));

        return services;
    }
}
