var builder = WebApplication.CreateBuilder(args);

// M0.1: minimal composition — liveness only, so the host runs and is probeable.
// Configuration layering, Serilog + correlation, OpenTelemetry, and readiness
// checks land in M0.4 per the milestone plan; module loading lands with M1.
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health/live");

app.Run();
