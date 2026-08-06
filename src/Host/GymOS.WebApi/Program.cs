using System.Globalization;
using Asp.Versioning;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Auditing;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
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

        if (context.HostingEnvironment.IsDevelopment())
        {
            loggerConfiguration.WriteTo.Console(formatProvider: CultureInfo.InvariantCulture);
        }
        else
        {
            loggerConfiguration.WriteTo.Console(new CompactJsonFormatter());
        }
    });

    builder.Services.AddBuildingBlocksInfrastructure();
    builder.Services.AddCqrsDispatcher();
    builder.Services.AddPlatformApplication();
    builder.Services.AddPlatformPersistence(builder.Configuration);
    builder.Services.AddPlatformIdentity(builder.Configuration);
    builder.Services.AddPlatformGyms();
    builder.Services.AddPlatformSubscriptions();

    builder.Services.AddGymOSAuthentication();
    builder.Services.AddGymOSRateLimiting(builder.Configuration);
    builder.Services.AddGymOSForwardedHeaders(builder.Configuration);

    // Second enforcement layer (AD-5): no handler runs without the permission its request
    // declares, even if an endpoint mapping forgot its policy.
    builder.Services.AddAuthorizationBehavior();

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
        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await startupScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        await startupScope.ServiceProvider.GetRequiredService<PlatformIdentitySeeder>().SeedAsync(CancellationToken.None);

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
    }

    // First in the pipeline: the rate limiter partitions on the client IP and every audit row
    // records it, so the address has to be corrected before anything reads it.
    app.UseGymOSForwardedHeaders(app.Configuration);

    app.UseExceptionHandler();
    app.UseStatusCodePages();

    app.UseMiddleware<CorrelationMiddleware>();
    app.UseSerilogRequestLogging();

    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");
    app.MapOpenApi();
    app.MapAuthEndpoints();
    app.MapGymEndpoints();
    app.MapPlanEndpoints();
    app.MapSubscriptionEndpoints();
    app.MapUserEndpoints();
    app.MapRoleEndpoints();
    app.MapAuditLogEndpoints();

    await app.RunAsync();
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
