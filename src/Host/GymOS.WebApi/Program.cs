using System.Globalization;
using Asp.Versioning;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Idempotency;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.Platform.Infrastructure.Tenancy;
using GymOS.WebApi.Analytics;
using GymOS.WebApi.Auth;
using GymOS.WebApi.Endpoints;
using GymOS.WebApi.Extensions;
using GymOS.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
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

    // `/dashboard/health` needs `HealthCheckService`, which ships in the ASP.NET Core shared
    // framework this host references — Application and Infrastructure are plain `Microsoft.NET.Sdk`
    // projects and cannot see it. Registered here, by hand, rather than folded into
    // AddPlatformAnalytics for that reason (see IDashboardHealthCheck).
    builder.Services.AddScoped<IDashboardHealthCheck, DashboardHealthCheckAdapter>();

    // The job runner (M3.4). Jobs register below it; Jobs:Enabled=false turns the host off for
    // tests that drive the sweeps by hand.
    builder.Services.AddJobHost();

    builder.Services.AddGymOSAuthentication();
    builder.Services.AddGymOSRateLimiting(builder.Configuration);
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
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database");

    // OpenTelemetry (AD-38): traces + metrics; OTLP export activates only when an
    // endpoint is configured (production: Azure Monitor / any OTLP backend).
    IOpenTelemetryBuilder openTelemetry = builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: "gymos-backend",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString()))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddSource(MessagingDiagnostics.ActivitySourceName))
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation());

    string? otlpEndpoint = builder.Configuration["OpenTelemetry:OtlpEndpoint"];
    if (!string.IsNullOrWhiteSpace(otlpEndpoint))
    {
        openTelemetry.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(otlpEndpoint));
    }

    var app = builder.Build();

    // Development convenience: apply migrations at startup when configured
    // (production applies them from the deployment pipeline). The identity catalog
    // sync always runs — idempotent, and it skips with a warning when migrations
    // are pending (AD-22 declare-and-validate).
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

    // First in the pipeline: the rate limiter partitions on the client IP and every audit row
    // records it, so the address has to be corrected before anything reads it.
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

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    // After authorization, because it reads the caller's grants; before anything opens a connection,
    // because TenantSessionInterceptor writes whatever this leaves on the context. Getting the order
    // wrong fails closed — no tenant, no rows — which is the safe direction and looks like an empty
    // screen rather than an error.
    app.UseMiddleware<TenantResolutionMiddleware>();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");

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
