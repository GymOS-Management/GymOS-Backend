using System.Globalization;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.DependencyInjection;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.WebApi.Middleware;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
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
    builder.Services.AddPlatformPersistence(builder.Configuration);

    // OpenAPI document generation (AD-12/AD-37): the contract-first seam. Versioned
    // endpoint groups and the CI breaking-change gate land with the first v1 endpoints.
    builder.Services.AddOpenApi();

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
    }

    app.UseMiddleware<CorrelationMiddleware>();
    app.UseSerilogRequestLogging();

    app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
    app.MapHealthChecks("/health/ready");
    app.MapOpenApi();

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
