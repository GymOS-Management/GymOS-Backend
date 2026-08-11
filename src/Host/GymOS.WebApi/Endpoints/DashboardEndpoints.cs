using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Analytics;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/dashboard</c> — the console's numbers, read live (ARCHITECTURE.md §3.2).
///
/// <para>AD-8's daily read model exists for the analytics <em>series</em>, not this: a dashboard
/// reporting yesterday's gym count would not be a performance trade-off, it would be a wrong
/// answer.</para>
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Dashboard").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/dashboard")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Dashboard");

        group.MapGet("/summary", SummaryAsync)
            .RequireAuthorization(PlatformPermissions.AnalyticsRead)
            .WithName("GetDashboardSummary")
            .WithSummary("The dashboard's headline numbers, live")
            .WithDescription(
                "Gym counts and MRR, computed from the operational tables at request time — not "
                    + "from AD-8's daily read model. A summary served from yesterday's row would not "
                    + "be a trade-off, it would be wrong. `mrr` carries one entry per currency; "
                    + "amounts in different currencies are never summed or converted (AD-43 is "
                    + "undecided).")
            .Produces<DashboardSummary>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/health", HealthAsync)
            .RequireAuthorization(PlatformPermissions.AnalyticsRead)
            .WithName("GetDashboardHealth")
            .WithSummary("The platform's health checks, for a console panel")
            .WithDescription(
                "The same checks `/health/ready` runs, projected to each check's name and status "
                    + "only. No exception details and no descriptions — either can carry a "
                    + "connection string or a stack trace, and this endpoint is for a status panel, "
                    + "not a debugger.")
            .Produces<DashboardHealth>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/activity", ActivityAsync)
            .RequireAuthorization(PlatformPermissions.AuditRead)
            .WithName("GetRecentActivity")
            .WithSummary("The audit log, trimmed, for the dashboard's activity feed")
            .WithDescription(
                "The newest audit entries, in a shape sized for a feed rather than the full audit "
                    + "surface. Gated on `audit.read`, not `analytics.read` — this is the audit log "
                    + "wearing a different hat, and audit access must not depend on analytics access, "
                    + "or the reverse. `limit` defaults to 20 and is clamped between 1 and 100.")
            .Produces<IReadOnlyList<ActivityEntry>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> SummaryAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<DashboardSummary> result =
            await dispatcher.QueryAsync(new GetDashboardSummaryQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> HealthAsync(
        HealthCheckService healthCheckService, CancellationToken cancellationToken)
    {
        HealthReport report = await healthCheckService.CheckHealthAsync(cancellationToken);

        // No Exception, no Description projected here: this endpoint is authenticated, but an
        // exception message can carry a connection string, which is an information disclosure a
        // console panel has no business exposing.
        var health = new DashboardHealth(
            report.Status.ToString(),
            report.Entries
                .Select(entry => new HealthCheckStatus(entry.Key, entry.Value.Status.ToString()))
                .ToList());

        return TypedResults.Ok(health);
    }

    private static async Task<IResult> ActivityAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        int limit = 20)
    {
        Result<IReadOnlyList<ActivityEntry>> result =
            await dispatcher.QueryAsync(new GetRecentActivityQuery(limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
