using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/gyms/{gymId}/subscription</c> — what one gym pays (ARCHITECTURE.md §3.3).
///
/// Nested under the gym because a subscription has no meaning apart from it: there is at most one live
/// subscription per gym, so the gym's id identifies it and no separate collection is needed.
/// </summary>
public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Gym subscriptions").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/gyms/{gymId:guid}/subscription")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Gym subscriptions");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("GetGymSubscription")
            .WithSummary("A gym's current subscription")
            .WithDescription(
                "Returns the live subscription — trialing, active, or past due. 404 when the gym has "
                    + "none, which is a normal state: a gym can exist before it is sold anything. The "
                    + "price returned is the one agreed at assignment, not the plan's current price.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/history", GetHistoryAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("ListGymSubscriptionHistory")
            .WithSummary("Every subscription this gym has had")
            .WithDescription(
                "Newest first, including ended ones. Subscriptions are never deleted — they are what a "
                    + "billing dispute is settled from.")
            .Produces<IReadOnlyList<SubscriptionDetail>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", AssignAsync)
            .RequireAuthorization(PlatformPermissions.SubscriptionsManage)
            .HonoursIfMatch()
            .WithName("AssignGymSubscription")
            .WithSummary("Put the gym on a plan, or move it to another")
            .WithDescription(
                "One operation for both: assigns a subscription when the gym has none, and changes the "
                    + "plan or terms when it does. `billingCycle` and `currency` must match a price the "
                    + "plan actually offers — an absent combination is refused rather than approximated, "
                    + "since guessing would bill an amount nobody agreed to.\n\n"
                    + "A withdrawn plan cannot be newly assigned, though a gym already on one keeps it. "
                    + "The agreed price is **copied**, so later repricing of the plan never changes what "
                    + "this gym pays. Changing plan leaves the paid period alone; charging or crediting "
                    + "the difference is proration, which arrives with invoices in M3.3.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/cancel", CancelAsync)
            .RequireAuthorization(PlatformPermissions.SubscriptionsManage)
            .HonoursIfMatch()
            .WithName("CancelGymSubscription")
            .WithSummary("Cancel the subscription")
            .WithDescription(
                "By default schedules cancellation for the end of the paid period, so the gym keeps what "
                    + "it has paid for and `cancelAtPeriodEnd` becomes true. Send `immediately: true` to "
                    + "end it now and forfeit the remainder — a deliberate choice. Ending it now is "
                    + "terminal: a gym that returns gets a new subscription, leaving this one as the "
                    + "record of what it had.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/resume", ResumeAsync)
            .RequireAuthorization(PlatformPermissions.SubscriptionsManage)
            .HonoursIfMatch()
            .WithName("ResumeGymSubscription")
            .WithSummary("Withdraw a scheduled cancellation")
            .WithDescription(
                "Clears `cancelAtPeriodEnd` while the period is still running. It cannot revive a "
                    + "subscription that has already ended.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/convert-trial", ConvertTrialAsync)
            .RequireAuthorization(PlatformPermissions.SubscriptionsManage)
            .HonoursIfMatch()
            .WithName("ConvertGymTrial")
            .WithSummary("End the trial and start billing")
            .WithDescription(
                "Explicit for now: the renewal job (M3.4) will do this on the trial's end date. Until "
                    + "then a trial that has run out stays `Trialing` with `trialEndsAtUtc` in the past, "
                    + "which is legible rather than wrong.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/payment-state", SetPaymentStateAsync)
            .RequireAuthorization(PlatformPermissions.SubscriptionsManage)
            .HonoursIfMatch()
            .WithName("SetGymSubscriptionPaymentState")
            .WithSummary("Record that payment is late, or has arrived")
            .WithDescription(
                "`pastDue: true` marks it late; `false` marks it paid and returns it to active. "
                    + "**Service is not withheld either way** — whether a late-paying gym keeps operating "
                    + "is a separate decision, taken by suspending the gym. Every failed card must not "
                    + "end a business's day.")
            .Produces<SubscriptionDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        Guid gymId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.QueryAsync(
            new GetGymSubscriptionQuery(gymId), cancellationToken);

        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> GetHistoryAsync(
        Guid gymId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<SubscriptionDetail>> result = await dispatcher.QueryAsync(
            new ListGymSubscriptionHistoryQuery(gymId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> AssignAsync(
        Guid gymId,
        AssignSubscriptionRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.SendAsync(
            new AssignSubscriptionCommand(
                gymId,
                request.PlanId,
                request.BillingCycle,
                request.Currency,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        // 200 rather than 201 even when this creates the subscription: the resource is the gym's
        // subscription, which is addressable at this same URL either way, so a caller never has to
        // branch on whether it existed (API_STANDARDS.md §2 — PUT returns the updated resource).
        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> CancelAsync(
        Guid gymId,
        CancelSubscriptionRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.SendAsync(
            new CancelSubscriptionCommand(
                gymId,
                request.Reason,
                request.Immediately,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ResumeAsync(
        Guid gymId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.SendAsync(
            new ResumeSubscriptionCommand(gymId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ConvertTrialAsync(
        Guid gymId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.SendAsync(
            new ConvertTrialCommand(gymId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SetPaymentStateAsync(
        Guid gymId,
        SetPaymentStateRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SubscriptionDetail> result = await dispatcher.SendAsync(
            new SetSubscriptionPaymentStateCommand(
                gymId,
                request.PastDue,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }
}
