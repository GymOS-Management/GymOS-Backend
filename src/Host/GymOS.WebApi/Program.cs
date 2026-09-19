using System.Globalization;
using Asp.Versioning;
using Azure.Identity;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.Platform.Infrastructure.Persistence.Seeding.DemoData;
using GymOS.Platform.Infrastructure.Tenancy;
using GymOS.WebApi.Analytics;
using GymOS.WebApi.Auth;
using GymOS.WebApi.Endpoints;
using GymOS.WebApi.Extensions;
using GymOS.WebApi.Health;
using GymOS.WebApi.Middleware;
using GymOS.WebApi.Monitoring;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Compact;

// Bootstrap logger: catches host-startup failures before full configuration loads.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Structured logging (AD-38): human-readable console in Development, compact
    // JSON everywhere else (container-native, ingestible by App Insights/Seq).
    // Levels come from the Serilog configuration section — tunable per environment.
    builder.Host.UseSerilog((context, _, loggerConfiguration) =>
    {
        loggerConfiguration
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "gymos-backend");

        // Format is environment-derived by default and overridable by configuration
        // (`Serilog:ConsoleFormat` = json | text). The override exists for containers: the same
        // image runs in every environment, and an operator debugging a container wants readable
        // lines while a log collector wants JSON — with no way to choose, one of them is always
        // wrong. Neither is a different *logger*; only the console formatter differs.
        string? configured = context.Configuration["Serilog:ConsoleFormat"];

        bool useJson = configured switch
        {
            not null when configured.Equals("json", StringComparison.OrdinalIgnoreCase) => true,
            not null when configured.Equals("text", StringComparison.OrdinalIgnoreCase) => false,
            _ => !context.HostingEnvironment.IsDevelopment(),
        };

        if (useJson)
        {
            loggerConfiguration.WriteTo.Console(new CompactJsonFormatter());
        }
        else
        {
            loggerConfiguration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }
    });

    builder.Services.AddBuildingBlocksInfrastructure();
    builder.Services.AddCqrsDispatcher();
    builder.Services.AddPlatformApplication();
    builder.Services.AddPlatformPersistence(builder.Configuration);
    builder.Services.AddPlatformIdentity(builder.Configuration);
    builder.Services.AddPlatformGyms();
    builder.Services.AddPlatformSubscriptions();
    builder.Services.AddPlatformFeatures();
    builder.Services.AddPlatformAnalytics();

    // M6 monitoring: the job host's run recorder and the monitoring read side.
    builder.Services.AddPlatformMonitoring(builder.Configuration);

    // M7 settings: the declared catalog over platform_settings, and Data Protection for sensitive values.
    builder.Services.AddPlatformSettings(builder.Configuration);

    // Key Vault wraps the key ring only when the owner has created the vault and set the key id
    // (AZURE.md §7). Inert otherwise — the config value is the switch.
    if (builder.Configuration["DataProtection:KeyVaultKeyId"] is { Length: > 0 } keyVaultKeyId)
    {
        if (!Uri.TryCreate(keyVaultKeyId, UriKind.Absolute, out Uri? keyId))
        {
            throw new InvalidOperationException(
                "DataProtection:KeyVaultKeyId must be an absolute URI such as https://<vault>.vault.azure.net/keys/<name>/<version>.");
        }

        builder.Services.AddDataProtection().ProtectKeysWithAzureKeyVault(keyId, new DefaultAzureCredential());
    }

    // `/dashboard/health` needs `HealthCheckService`, which ships in the ASP.NET Core shared
    // framework this host references — Application and Infrastructure are plain `Microsoft.NET.Sdk`
    // projects and cannot see it. Registered here, by hand, rather than folded into
    // AddPlatformAnalytics for that reason (see IDashboardHealthCheck).
    builder.Services.AddScoped<IDashboardHealthCheck, DashboardHealthCheckAdapter>();

    // M6: this replica's own process metrics. Host-side for the same reason as the health adapter
    // (System.Diagnostics.Process is not an Application concern). One instance, both roles.
    builder.Services.TryAddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ProcessMetricsSampler>();
    builder.Services.AddSingleton<IProcessMetrics>(provider => provider.GetRequiredService<ProcessMetricsSampler>());
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ProcessMetricsSampler>());

    // The job runner (M3.4). Jobs register below it; Jobs:Enabled=false turns the host off for
    // tests that drive the sweeps by hand.
    builder.Services.AddJobHost();

    builder.Services.AddGymOSAuthentication();
    builder.Services.AddGymOSRateLimiting();
    builder.Services.AddGymOSForwardedHeaders(builder.Configuration);

    // Second enforcement layer (AD-5): no handler runs without the permission its request
    // declares, even if an endpoint mapping forgot its policy.
    builder.Services.AddAuthorizationBehavior();

    // AD-39: claims/replays idempotent commands. Registered after authorization so a stored
    // response is never returned to a caller who could not execute the operation now.
    builder.Services.AddIdempotencyBehavior();

    // URL-segment versioning (AD-37): /api/v1/…. Unversioned callers are not guessed
    // at — an explicit version is part of the contract.
    builder.Services.AddApiVersioning(options =>
    {
        options.ApiVersionReader = new UrlSegmentApiVersionReader();
        options.DefaultApiVersion = new ApiVersion(1, 0);
        options.AssumeDefaultVersionWhenUnspecified = false;
        options.ReportApiVersions = true;
    });

    // RFC 7807 for every error response, including the ones the framework produces
    // (404, 405, 415) — one error shape across the whole API (ARCHITECTURE.md §3).
    builder.Services.AddProblemDetails();
    builder.Services.AddGymOSExceptionHandling();

    // OpenAPI document generation (AD-12/AD-37): the contract-first seam, one document
    // per API version. The CI breaking-change gate diffs it against its committed
    // snapshot (M1.3).
    builder.Services.AddGymOSOpenApi("v1");
    builder.Services.AddGymOSApiReferenceBrowser(builder.Configuration, builder.Environment);

    // Readiness gates on the database only (AD-48); liveness has no dependencies.
    //
    // ONE registration, not two. It replaces AddDbContextCheck<AppDbContext>("database"), whose
    // CanConnectAsync is true of an empty database, a database three migrations behind, and a
    // correct one alike — see DatabaseReadinessHealthCheck for why "reachable" is the wrong question
    // and what it asks instead. Keeping the name `database` keeps /dashboard/health's payload
    // unchanged.
    //
    // Registering a second check alongside it was the obvious shape and is the wrong one here: the
    // health-check service runs registrations concurrently, each in its own scope with its own
    // DbContext, so a second check makes every host's SqlClient pool open a second physical connection
    // the first time anything probes /health/ready. The integration suite boots on the order of a
    // hundred hosts against one container, and that alone took the run past `max_connections=300` —
    // 122 failures with `53300: remaining connection slots are reserved for roles with the SUPERUSER
    // attribute`, in tests that had nothing to do with health. Measured, not predicted; see
    // PostgresFixture, which already carries two other instances of the same lesson.
    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseReadinessHealthCheck>("database");

    // OpenTelemetry (AD-38): traces + metrics; OTLP export activates only when an
    // endpoint is configured (production: Azure Monitor / any OTLP backend).
    IOpenTelemetryBuilder openTelemetry = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: "gymos-backend",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation()
            .AddSource(MessagingDiagnostics.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation());

    string? otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        openTelemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(otlpEndpoint));
    }

    var app = builder.Build();

    // Before anything opens a connection, and before the migrator branch below: does this host's
    // environment match the database it is pointed at? Development owns GymOS_Local, Staging owns
    // GymOS_UAT, Production owns GymOS_Production, and no host may open another environment's
    // database. Placed above `Database:MigrateOnly` deliberately — the migrator applies DDL, so it is
    // the process that must least be allowed to do it to the wrong environment.
    //
    // Fatal, like R14's validator, and for a comparable reason: a host serving the wrong environment
    // is not a degraded service, it is correct behaviour applied to the wrong data, and nothing
    // downstream will notice. See DatabaseEnvironmentGuard for why it denies cross-environment
    // pairings rather than prescribing a database per environment.
    DatabaseEnvironmentGuard.Validate(
        app.Environment.EnvironmentName,
        app.Configuration.GetConnectionString("Database")
            ?? throw new InvalidOperationException(
                "Connection string 'Database' is not configured. Set ConnectionStrings:Database via "
                    + "appsettings, environment (ConnectionStrings__Database), or the secret store."));

    // Startup work, in the order the next step depends on the previous one.
    //
    // Migrations are NOT this process's job in any environment: the one-shot `migrator` service
    // (`Database:MigrateOnly`, running as gymos_migrator) owns them, in development and production
    // alike, and `api` waits for it to exit 0. `Database:MigrateOnStartup` remains as an escape
    // hatch for a host driving its own database — the integration suite is the only user — and is
    // `false` in every compose file and appsettings file in the repository.
    //
    // Everything below the migration branches skips itself while migrations are pending, each with
    // a warning: the identity and feature catalog syncs, the SuperAdmin seeder, R14's validator, and
    // both maintenance sweeps. That is a whole boot's worth of controls silently off, which is why
    // PendingMigrationsHealthCheck (registered above) makes readiness fail for the same condition
    // rather than leaving those warnings as the only trace.
    using (IServiceScope startupScope = app.Services.CreateScope())
    {
        // The migrator's mode: apply migrations, then stop. Nothing else runs — the seeders write
        // data and the R14 validator checks the role that serves requests, and neither is this
        // role's job. Deployment runs this as `gymos_migrator`, which owns the schema; the API then
        // starts as `gymos_app`, which owns nothing.
        //
        // The exit code is load-bearing. Compose gates `api` on this container finishing
        // successfully, so exiting 0 after a failed migration would start the API against an
        // unmigrated database — where R14's validator hits its pending-migrations guard and switches
        // the whole control off for that boot. The surrounding try/catch already logs Fatal and
        // rethrows, which produces a non-zero exit; this must stay inside it.
        if (app.Configuration.GetValue<bool>("Database:MigrateOnly"))
        {
            await startupScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();

            Log.Information("Migrations applied; exiting because Database:MigrateOnly is set.");
            return 0;
        }

        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await startupScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        // R14, before anything reads or writes: a tenant-scoped table without a forced policy means
        // isolation is off, and the only safe response is not to serve traffic. Fatal by design —
        // see TenantIsolationValidator.
        await startupScope.ServiceProvider
            .GetRequiredService<TenantIsolationValidator>()
            .ValidateAsync(CancellationToken.None);

        await startupScope.ServiceProvider.GetRequiredService<PlatformIdentitySeeder>().SeedAsync(CancellationToken.None);

        // The feature catalogue (§2.4). Insert-only: a feature's default and copy belong to the
        // operator once its row exists, so this adds keys the code has newly declared and leaves
        // everything else exactly as an administrator left it.
        await startupScope.ServiceProvider.GetRequiredService<FeatureCatalogSeeder>().SeedAsync(CancellationToken.None);

        // Runs after the catalog sync: the SuperAdmin needs its role to exist first.
        // Does nothing unless Seed:SuperAdmin is configured, and never resets an
        // existing account's password.
        await startupScope.ServiceProvider.GetRequiredService<SuperAdminSeeder>().SeedAsync(CancellationToken.None);

        // The demo-data mode: fill an empty portfolio, then stop — the same shape as the migrator's
        // mode above, and placed here so the catalogue the demo plans reference already exists.
        // Runs as the application principal because everything it writes is ordinary DML. Refuses
        // Production, throws on pending migrations, and writes nothing when any gym or plan exists;
        // see DemoDataSeeder and docs/superpowers/specs/2026-09-03-demo-data-seeder-design.md.
        if (app.Configuration.GetValue<bool>("Seed:DemoData"))
        {
            await startupScope.ServiceProvider
                .GetRequiredService<DemoDataSeeder>()
                .SeedAsync(app.Environment.EnvironmentName, CancellationToken.None);

            Log.Information("Demo data seeding finished; exiting because Seed:DemoData is set.");
            return 0;
        }

        // Monthly audit partitions, created ahead of need (§2.6). Idempotent and
        // non-fatal: the DEFAULT partition keeps audit writes working if this fails.
        // Moves onto the retention job's schedule when the job runner lands (AD-23).
        if (app.Services.GetRequiredService<IOptions<AuditLogPartitionOptions>>().Value.RunOnStartup)
        {
            await startupScope.ServiceProvider
                .GetRequiredService<AuditLogPartitionMaintainer>()
                .EnsurePartitionsAsync(CancellationToken.None);
        }

        // AD-39: deletes expired idempotency records ahead of the M3.4 retention job, same
        // startup-owned, non-fatal pattern as the audit partition maintainer above.
        if (app.Services.GetRequiredService<IOptions<IdempotencyOptions>>().Value.PurgeOnStartup)
        {
            await startupScope.ServiceProvider
                .GetRequiredService<IdempotencyRecordMaintainer>()
                .PurgeExpiredAsync(CancellationToken.None);
        }
    }

    // First in the pipeline: several downstream steps (the rate limiter's IP partition, every
    // audit row) read the client address, so it has to be corrected before anything reads it.
    app.UseGymOSForwardedHeaders(app.Configuration);

    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.UseMiddleware<CorrelationMiddleware>();
    app.UseMiddleware<IdempotencyHeaderMiddleware>();
    app.UseSerilogRequestLogging(options =>
        // The default level rule, with one carve-out: a request the CALLER abandoned logs as
        // information, not error. This middleware sits inside UseExceptionHandler, so it sees
        // the cancellation on the way out before the 499 handler converts it — without the
        // carve-out every aborted fetch writes an ERR line for a request nobody was waiting on.
        options.GetLevel = (httpContext, _, exception) =>
            exception is OperationCanceledException
                && httpContext.RequestAborted.IsCancellationRequested
                ? Serilog.Events.LogEventLevel.Information
                : exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? Serilog.Events.LogEventLevel.Error
                    : Serilog.Events.LogEventLevel.Information);

    app.UseAuthentication();

    // After authentication (Task 10): the global limiter's partition selector reads
    // HttpContext.User to key an authenticated caller by user id rather than address, and before
    // authentication that principal is always empty. The sign-in policy is unaffected — it keys
    // on the client IP regardless.
    app.UseRateLimiter();

    // M7 spec §6: after authentication, so IPermissionEvaluator can resolve who the caller is;
    // before authorization, so a refused caller is turned away with 503 before any route policy or
    // handler runs.
    app.UseMiddleware<MaintenanceModeMiddleware>();

    app.UseAuthorization();

    // After authorization, because it reads the caller's grants; before anything opens a connection,
    // because TenantSessionInterceptor writes whatever this leaves on the context. Getting the order
    // wrong fails closed — no tenant, no rows — which is the safe direction and looks like an empty
    // screen rather than an error.
    app.UseMiddleware<TenantResolutionMiddleware>();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");

    // Which build is answering. Beside the health endpoints rather than among the /api/v1 groups
    // below, because it belongs to the same operational surface: unversioned, anonymous, and absent
    // from the OpenAPI document. See VersionEndpoints for why each of those three is deliberate.
    app.MapVersionEndpoint();

    // The contract and its reference UI travel together — publishing one without the other
    // serves a UI that cannot load its own document. Off outside Development unless
    // OpenApi:Enabled says otherwise; see OpenApiExtensions.IsOpenApiExposed.
    if (OpenApiExtensions.IsOpenApiExposed(app.Configuration, app.Environment))
    {
        app.MapOpenApi();
        app.MapScalarApiReference(options => options
            .WithTitle("GymOS API v1")
            .WithOpenApiRoutePattern("/openapi/v1.json"));
    }

    app.MapAuthEndpoints();
    app.MapGymEndpoints();
    app.MapPlanEndpoints();
    app.MapSubscriptionEndpoints();
    app.MapInvoiceEndpoints();
    app.MapFeatureEndpoints();
    app.MapDashboardEndpoints();
    app.MapAnalyticsEndpoints();
    app.MapMonitoringEndpoints();
    app.MapSettingsEndpoints();
    app.MapUserEndpoints();
    app.MapRoleEndpoints();
    app.MapAuditLogEndpoints();

    await app.RunAsync();

    return 0;
}
catch (Exception exception)
{
    Log.Fatal(exception, "GymOS host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposes the entry point to WebApplicationFactory-based integration tests.</summary>
public partial class Program
{
}
