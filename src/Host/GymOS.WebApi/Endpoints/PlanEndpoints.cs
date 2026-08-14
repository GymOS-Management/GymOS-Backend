using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/plans</c> — the subscription catalogue (ARCHITECTURE.md §3.4).
///
/// Every route declares its permission (R11, deny by default), and the same permission is declared on
/// each command, so a route that lost its policy still cannot reach a handler (AD-5).
/// </summary>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Subscription plans").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/plans")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Subscription plans");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("ListPlans")
            .WithSummary("List subscription plans")
            .WithDescription(
                "Paged. `search` matches name and code; `isPublic` filters listed versus unlisted plans. "
                    + "Withdrawn (inactive) plans are excluded unless `includeInactive=true` — the "
                    + "catalogue an operator means is the one they can sell from. `pageSize` is capped "
                    + "server-side.")
            .Produces<PagedResult<PlanListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{planId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("GetPlan")
            .WithSummary("One plan, with its prices")
            .WithDescription(
                "Prices are returned ordered by period length. An inactive plan still resolves: an "
                    + "operator asking why a gym is on a plan missing from the catalogue needs to see "
                    + "it. Subscribed-gym counts and feature sets arrive with M3.2 and M4 rather than "
                    + "appearing here as zeroes.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .WithName("CreatePlan")
            .WithSummary("Create a subscription plan")
            .WithDescription(
                "The plan starts active and public with no prices; add prices separately. The code is "
                    + "permanent and must be unique — it identifies the plan in exports and "
                    + "integrations. Limits left empty mean unlimited. Returns the created plan and its "
                    + "`ETag`, so no follow-up GET is needed.")
            .Produces<PlanDetail>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{planId:guid}", UpdateAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .RequiresIfMatch()
            .WithName("UpdatePlan")
            .WithSummary("Change a plan's details")
            .WithDescription(
                "Code is not editable — it is the plan's permanent handle. Requires `If-Match` carrying "
                    + "the `rowVersion` you read: without it the request is refused with 428, and a "
                    + "stale version with 409. Returns the updated plan and its new `ETag`.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPut("/{planId:guid}/prices", SetPriceAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .RequiresIfMatch()
            .WithName("SetPlanPrice")
            .WithSummary("Set the price for one cycle and currency")
            .WithDescription(
                "Adds the price, or changes it if one already exists for that cycle and currency — one "
                    + "intent, so one operation. Prices are rows, so offering annual billing or a second "
                    + "currency is data entry rather than a schema change. **Existing subscriptions are "
                    + "unaffected**: they hold a price snapshot, so a change applies to new assignments "
                    + "and renewals only. `If-Match` is **required**: this overwrites a price you read, "
                    + "and it is an upsert with no state machine to refuse a stale interleaving, so "
                    + "without it one operator's price silently replaces another's. Returns the "
                    + "updated plan.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapDelete("/{planId:guid}/prices/{planPriceId:guid}", RemovePriceAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .RequiresIfMatch()
            .WithName("RemovePlanPrice")
            .WithSummary("Withdraw a price")
            .WithDescription(
                "Removes the offer for that cycle and currency. Safe for existing subscriptions, which "
                    + "snapshot the price they were assigned. `If-Match` is **required**, like the "
                    + "upsert above: withdrawing a price you may not have seen repriced is the same "
                    + "lost-update hazard from the other direction. Returns the updated plan.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/{planId:guid}/features", GetFeaturesAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("GetPlanFeatures")
            .WithSummary("What this plan includes")
            .WithDescription(
                "The features this plan has an **opinion** about (§2.4). A feature absent from the "
                    + "list is not excluded — it is unmentioned, and gyms on this plan fall through "
                    + "to the platform default for it. That third state is why resolution has three "
                    + "layers. Returned as part of the plan, so the response is the plan itself.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{planId:guid}/features", SetFeaturesAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .RequiresIfMatch()
            .WithName("SetPlanFeatures")
            .WithSummary("Set what this plan includes")
            .WithDescription(
                "Each entry names a feature `key` and an `isEnabled` of `true`, `false`, or **`null` "
                    + "to clear the plan's opinion** — which makes gyms on the plan fall through to "
                    + "the platform default, a different answer from `false`. Only the keys you send "
                    + "are touched, so a client that knows about ten features cannot erase an "
                    + "eleventh added by a later release. An unknown key rejects the whole request: "
                    + "a half-applied feature set is worse than a refused one. Gated on "
                    + "`billing.manage` because what a plan includes is priced — granting one gym an "
                    + "exception is `features.manage` instead. `If-Match` is **required**.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{planId:guid}/deactivate", DeactivateAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .HonoursIfMatch()
            .WithName("DeactivatePlan")
            .WithSummary("Withdraw a plan from the catalogue")
            .WithDescription(
                "**This is the closest thing to a delete that exists.** Plans are never destroyed — "
                    + "subscriptions and invoices reference them, and destroying one would orphan the "
                    + "record of what a gym agreed to pay. Gyms already on the plan keep it and keep "
                    + "billing; it simply cannot be newly assigned. Returns the updated plan.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{planId:guid}/activate", ActivateAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .HonoursIfMatch()
            .WithName("ActivatePlan")
            .WithSummary("Return a plan to the catalogue")
            .WithDescription("Makes a withdrawn plan assignable again. Returns the updated plan.")
            .Produces<PlanDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        int? page = null,
        int? pageSize = null,
        string? sortBy = null,
        string? sortDir = null,
        string? search = null,
        bool includeInactive = false,
        bool? isPublic = null)
    {
        Result<PagedResult<PlanListItem>> result = await dispatcher.QueryAsync(
            new ListPlansQuery(
                new PageRequest(page, pageSize, sortBy, sortDir, search),
                includeInactive,
                isPublic),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid planId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.QueryAsync(new GetPlanQuery(planId), cancellationToken);

        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> CreateAsync(
        CreatePlanRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new CreatePlanCommand(
                request.Code,
                request.Name,
                request.Description,
                request.TrialDays,
                request.MaxMembers,
                request.MaxTrainers,
                request.MaxStaff,
                request.StorageLimitMb,
                request.SortOrder),
            cancellationToken);

        return WriteResults.Created(
            result,
            location: result.IsSuccess ? $"/api/v1/plans/{result.Value.Id}" : string.Empty,
            rowVersion: result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> UpdateAsync(
        Guid planId,
        UpdatePlanRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new UpdatePlanCommand(
                planId,
                request.Name,
                request.Description,
                request.TrialDays,
                request.MaxMembers,
                request.MaxTrainers,
                request.MaxStaff,
                request.StorageLimitMb,
                request.SortOrder,
                request.IsPublic,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> GetFeaturesAsync(
        Guid planId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // The plan carries its own feature set, so "what does this plan include" is the plan.
        Result<PlanDetail> result = await dispatcher.QueryAsync(new GetPlanQuery(planId), cancellationToken);

        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SetFeaturesAsync(
        Guid planId,
        SetPlanFeaturesRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        // The missing-field refusal lives in the handler, not here: in the delegate it preempted the
        // handler's own 404 and 428 and left non-HTTP callers unguarded.

        Result<PlanDetail> result = await dispatcher.SendAsync(
            new SetPlanFeaturesCommand(
                planId,
                request.Features,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SetPriceAsync(
        Guid planId,
        SetPlanPriceRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new SetPlanPriceCommand(
                planId,
                request.BillingCycle,
                request.Currency,
                request.Amount,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> RemovePriceAsync(
        Guid planId,
        Guid planPriceId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new RemovePlanPriceCommand(
                planId,
                planPriceId,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        // 200 with the plan, not 204: the price is part of the plan's representation, and the caller's
        // next question is "what does the plan look like now" (API_STANDARDS.md §2).
        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> DeactivateAsync(
        Guid planId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new DeactivatePlanCommand(planId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ActivateAsync(
        Guid planId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<PlanDetail> result = await dispatcher.SendAsync(
            new ActivatePlanCommand(planId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }
}
