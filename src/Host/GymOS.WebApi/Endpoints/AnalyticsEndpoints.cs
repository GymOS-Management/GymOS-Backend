using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Analytics;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/analytics</c> — the recorded series behind the dashboard's charts (ARCHITECTURE.md §3.8).
///
/// <para>Every route here reads only <c>daily_platform_stats</c> (AD-8), and only the days it holds:
/// a missing day comes back as a gap, never as a zero. A zero reads as "we lost every gym that
/// night"; a consumer that wants a continuous axis can see the gap and decide for itself what to do
/// with it.</para>
///
/// <para>There is deliberately no <c>granularity</c> parameter. The read model is daily, and a
/// weekly/monthly rollup is a client-side sum of the daily points until someone has a reason it
/// should be computed here instead.</para>
/// </summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Analytics").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/analytics")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Analytics");

        group.MapGet("/revenue", RevenueAsync)
            .RequireAuthorization(PlatformPermissions.AnalyticsRead)
            .WithName("GetRevenue")
            .WithSummary("Daily MRR, recorded days only")
            .WithDescription(
                "One point per day the nightly job actually recorded, each carrying MRR per "
                    + "currency — amounts in different currencies are never summed or converted. A "
                    + "day with no row is omitted, not zero-filled: a missing day drawn as zero reads "
                    + "as \"we lost every gym that night\", which a gap does not. `from` and `to` are "
                    + "both required and inclusive, and the range covers at most 366 days.")
            .Produces<IReadOnlyList<RevenuePoint>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/gyms-growth", GymsGrowthAsync)
            .RequireAuthorization(PlatformPermissions.AnalyticsRead)
            .WithName("GetGymsGrowth")
            .WithSummary("Daily gym counts, recorded days only")
            .WithDescription(
                "One point per recorded day: total, active, suspended, provisioning, and new gyms. A "
                    + "day with no row is omitted, not zero-filled — see the endpoint group's remarks. "
                    + "`from` and `to` are both required and inclusive, and the range covers at most "
                    + "366 days.")
            .Produces<IReadOnlyList<GrowthPoint>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/churn", ChurnAsync)
            .RequireAuthorization(PlatformPermissions.AnalyticsRead)
            .WithName("GetChurn")
            .WithSummary("Daily churn, canceled and expired kept apart")
            .WithDescription(
                "Cancellations and expiries are reported separately and deliberately never merged "
                    + "before the caller sees them: a cancellation is a decision the customer made, an "
                    + "expiry is a payment that failed, and they are different problems — `total` is "
                    + "offered so no caller has to add the two, not so anyone has to choose between "
                    + "them. A day with no row is omitted, not zero-filled. `from` and `to` are both "
                    + "required and inclusive, and the range covers at most 366 days.")
            .Produces<IReadOnlyList<ChurnPoint>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> RevenueAsync(
        DateOnly from,
        DateOnly to,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RevenuePoint>> result =
            await dispatcher.QueryAsync(new GetRevenueQuery(from, to), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GymsGrowthAsync(
        DateOnly from,
        DateOnly to,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<GrowthPoint>> result =
            await dispatcher.QueryAsync(new GetGymsGrowthQuery(from, to), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> ChurnAsync(
        DateOnly from,
        DateOnly to,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ChurnPoint>> result =
            await dispatcher.QueryAsync(new GetChurnQuery(from, to), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
