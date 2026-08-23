using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Auditing;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Outbox;
using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.BuildingBlocks.Infrastructure.Tenancy;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.Platform.Infrastructure.Jobs;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.Platform.Infrastructure.Tenancy;
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
    /// <summary>
    /// Schema the platform module owns, and where its migration history table lives. Shared with
    /// <see cref="Persistence.AppDbContextDesignTimeFactory"/> and the integration harness, which
    /// must place the history table identically or `dotnet ef` and the running host would disagree
    /// about which migrations are applied.
    /// </summary>
    public const string PlatformSchema = "platform";

    /// <summary>EF Core's default history table name, restated because the schema is not the default.</summary>
    public const string HistoryTableName = "__EFMigrationsHistory";

    /// <remarks>
    /// <para><b><c>EnableRetryOnFailure</c> is deliberately not set</b>, despite Azure SQL being the
    /// production target and transient-fault retry being the standard advice for it. Turning it on
    /// installs <c>SqlServerRetryingExecutionStrategy</c>, and that strategy refuses a user-initiated
    /// transaction outright — "the configured execution strategy does not support user-initiated
    /// transactions". <c>IUnitOfWork.BeginTransactionAsync</c> is exactly that, and both
    /// <c>PlatformIdentitySeeder</c> and <c>FeatureCatalogSeeder</c> take one on every boot, so
    /// switching this on here fails startup rather than hardening it.</para>
    /// <para>Adopting it means wrapping every <c>BeginTransactionAsync</c> call site in
    /// <c>Database.CreateExecutionStrategy().ExecuteAsync(…)</c>, which changes transaction semantics —
    /// the delegate has to become replayable. That is a behavioural change, so this migration does not
    /// make it; it is written up as the first follow-up in
    /// <c>docs/runbooks/managed-sqlserver-roles.md</c>.</para>
    /// </remarks>
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
        services.TryAddScoped<TenantSessionInterceptor>();
        services.TryAddScoped<PlatformIdentitySeeder>();
        services.TryAddScoped<TenantIsolationValidator>();

        services.TryAddScoped<TenantContext>();
        services.TryAddScoped<ITenantContext>(provider => provider.GetRequiredService<TenantContext>());
        services.TryAddScoped<ITenantContextSetter>(provider => provider.GetRequiredService<TenantContext>());

        // Insert-only: a feature's defaults and copy belong to the operator once the row exists
        // (§3.5), so this must never "reconcile" them — see FeatureCatalogSeeder.
        services.TryAddScoped<FeatureCatalogSeeder>();

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

        // Cross-instance job coordination rides the same database as everything else (M3.4) —
        // and so does the clock the sweeps decide with: durable verdicts (Overdue, Expired, a
        // billed renewal) must not depend on any one host's idea of the time.
        services.TryAddScoped<IJobLock, SqlServerJobLock>();
        services.TryAddScoped<IDatabaseClock, SqlServerDatabaseClock>();

        // Serializes constraints over a SET of rows, which optimistic tokens cannot express —
        // the last-active-SuperAdmin rule is the first consumer.
        services.TryAddScoped<IAdvisoryLock, SqlServerAdvisoryLock>();

        // Refresh tokens accumulate one row per refresh; the retention job deletes the unusable
        // ones (the purge IRefreshTokenRepository has always documented).
        services.TryAddScoped<RefreshTokenMaintainer>();
        services.TryAddScoped<ActivationTokenMaintainer>();

        // The recurring half of retention (AD-23) — the startup maintainers above cover first
        // boot; this covers every night after.
        services.AddScheduledJob<RetentionJob>();

        services.TryAddScoped<IAuditTrail>(provider => new AuditTrail(
            provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<ICurrentUser>(),
            provider.GetRequiredService<ICorrelationContext>()));

        services.AddDbContext<AppDbContext>((provider, options) => options
            .UseSqlServer(connectionString, sqlServer => sqlServer
                .MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name!)
                // The history table lives beside the schema it describes rather than in dbo. A
                // migrator principal that owns `platform` and `outbox` and nothing else can then
                // write it, which is what the two-principal split depends on — see
                // TenantIsolationValidator.
                .MigrationsHistoryTable(HistoryTableName, PlatformSchema))
            .UseSnakeCaseNamingConvention()
            // Order matters: timestamps are stamped before the audit interceptor reads
            // values, so created_at/updated_at appear in the captured diff.
            .AddInterceptors(
                provider.GetRequiredService<AuditableTimestampsInterceptor>(),
                provider.GetRequiredService<AuditSaveChangesInterceptor>(),
                provider.GetRequiredService<OutboxSaveChangesInterceptor>(),
                // Connection-level, not SaveChanges-level: it must run on every open, including the
                // opens that only read.
                provider.GetRequiredService<TenantSessionInterceptor>()));

        return services;
    }
}
