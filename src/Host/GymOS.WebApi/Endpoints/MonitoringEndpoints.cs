using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/monitoring</c> — health, job state, outbox depth and this replica's process (ARCHITECTURE.md
/// §3.7, narrowed by the M6 spec: no devices, no logs, no metric history — each deferred to a named
/// increment, none pretended).
/// </summary>
public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapMonitoringEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Monitoring").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/monitoring")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Monitoring");

        group.MapGet("/overview", OverviewAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("GetMonitoringOverview")
            .WithSummary("Health, job state, outbox depth and this replica's process, live")
            .WithDescription(
                "Four independently sourced blocks. `health` is the same projection `/dashboard/health` "
                    + "returns. `jobs` lists every registered job, including ones that have never run "
                    + "(`lastRun: null`); `stale` is computed on read from the schedule and the run "
                    + "history. `outbox` is depth only — lag and dead letters arrive with the outbox "
                    + "processor, and `note` says so. `process` is the answering replica's own CPU, memory "
                    + "and uptime, null until its first sample; fleet-wide numbers are App Insights'.")
            .Produces<MonitoringOverview>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/jobs", JobsAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("GetJobStates")
            .WithSummary("Every registered job's schedule, last run, next due time and stale flag")
            .WithDescription(
                "The overview's `jobs` block on its own, for a page that polls job state. A job is stale "
                    + "when its newest run is still running past one period plus grace, or when it has "
                    + "not succeeded within two periods plus grace (grace is `Monitoring:StaleGrace`, "
                    + "default 5 minutes). A job that has never run is not stale.")
            .Produces<IReadOnlyList<JobStateDto>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/jobs/{name}/runs", RunsAsync)
            .RequireAuthorization(PlatformPermissions.MonitoringRead)
            .WithName("ListJobRuns")
            .WithSummary("One job's run history, newest first, paged by time")
            .WithDescription(
                "`name` is a registered job, or one that still has history under an old name. `from` "
                    + "and `to` are optional and inclusive on the run's start time; `status` is one of "
                    + "Running, Succeeded, Failed; `limit` defaults to 50 and is clamped to 200. "
                    + "`hasMore` means older runs exist — page by moving `to` to the oldest "
                    + "`startedAtUtc` returned. A run's `error` is the exception's type and message "
                    + "only; the stack is in the structured log.")
            .Produces<JobRunsPage>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<MonitoringOverview> result =
            await dispatcher.QueryAsync(new GetMonitoringOverviewQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> JobsAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<JobStateDto>> result =
            await dispatcher.QueryAsync(new GetJobStatesQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> RunsAsync(
        string name,
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        string? status = null,
        int limit = 50)
    {
        Result<JobRunsPage> result =
            await dispatcher.QueryAsync(new ListJobRunsQuery(name, from, to, status, limit), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
