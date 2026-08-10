using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/invoices</c> and the gym tab <c>/api/v1/gyms/{gymId}/invoices</c> (ARCHITECTURE.md §3.4).
///
/// There is deliberately no <c>POST /invoices</c> and no <c>DELETE</c>: invoices are created as a
/// consequence of subscription events, and an issued one is never erased — voiding is a state, and
/// the row is the record.
/// </summary>
public static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Invoices").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/invoices")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Invoices");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("ListInvoices")
            .WithSummary("List invoices")
            .WithDescription(
                "Paged and filterable. `status` filters on Draft, Issued, Paid, Overdue, or Void — an "
                    + "unknown value is a 400, never a silently empty page. `gymId` narrows to one "
                    + "tenant; `overdue=true` means issued and past due **now**, derived from the "
                    + "clock rather than stored. `search` matches the invoice number.")
            .Produces<PagedResult<InvoiceListItem>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{invoiceId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("GetInvoice")
            .WithSummary("One invoice, with its lines")
            .WithDescription(
                "Lines carry their own amounts — negative on a credit line, which is how a prorated "
                    + "upgrade shows the unused time it credited. Returns the `ETag` for the optional "
                    + "`If-Match` on mark-paid and void.")
            .Produces<InvoiceDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{invoiceId:guid}/mark-paid", MarkPaidAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .WithName("MarkInvoicePaid")
            .WithSummary("Mark an invoice paid")
            .WithDescription(
                "Requires an `Idempotency-Key` header (AD-39): marking an invoice paid moves money, so "
                    + "a retried request replays the stored outcome instead of paying twice. The body "
                    + "carries the payment reference — a provider id, bank transfer description, or "
                    + "receipt number — which is required. Optional `If-Match`. Paying the current "
                    + "period's invoice returns a past-due subscription to active in the same "
                    + "transaction.")
            .Produces<InvoiceDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapPost("/{invoiceId:guid}/void", VoidAsync)
            .RequireAuthorization(PlatformPermissions.BillingManage)
            .WithName("VoidInvoice")
            .WithSummary("Void an unpaid invoice")
            .WithDescription(
                "Requires the `X-Confirm-Action` header carrying the invoice's number — the M2 "
                    + "deletion-confirmation pattern, because void is irreversible and adjacent to "
                    + "buttons that are not. The row stays: voided is a state, not a deletion, and "
                    + "\"this was withdrawn\" is exactly what the record must be able to show. A paid "
                    + "invoice cannot be voided.")
            .Produces<InvoiceDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // The gym tab: same module, own group, because it lives under the gym's URL space.
        RouteGroupBuilder gymGroup = endpoints
            .MapGroup("/api/v{version:apiVersion}/gyms/{gymId:guid}/invoices")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Invoices");

        gymGroup.MapGet("/", ListForGymAsync)
            .RequireAuthorization(PlatformPermissions.BillingRead)
            .WithName("ListGymInvoices")
            .WithSummary("Every invoice ever issued to one gym")
            .WithDescription(
                "Newest first, unpaged — the billing conversation with one gym reads top to bottom. "
                    + "An unknown gym returns an empty list rather than 404: \"no invoices\" is the "
                    + "truthful answer either way, and the gym endpoints are where a gym's existence "
                    + "is asked about.")
            .Produces<IReadOnlyList<InvoiceListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

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
        string? status = null,
        Guid? gymId = null,
        bool? overdue = null)
    {
        Result<PagedResult<InvoiceListItem>> result = await dispatcher.QueryAsync(
            new ListInvoicesQuery(new InvoiceListFilter(
                new PageRequest(page, pageSize, sortBy, sortDir, search),
                status,
                gymId,
                overdue)),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid invoiceId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<InvoiceDetail> result = await dispatcher.QueryAsync(
            new GetInvoiceQuery(invoiceId), cancellationToken);

        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> MarkPaidAsync(
        Guid invoiceId,
        MarkInvoicePaidRequest? request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<InvoiceDetail> result = await dispatcher.SendAsync(
            new MarkInvoicePaidCommand(
                invoiceId,
                request?.PaymentReference,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> VoidAsync(
        Guid invoiceId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<InvoiceDetail> result = await dispatcher.SendAsync(
            new VoidInvoiceCommand(
                invoiceId,
                ConfirmationHeaders.ConfirmAction(httpContext.Request),
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ListForGymAsync(
        Guid gymId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<InvoiceListItem>> result = await dispatcher.QueryAsync(
            new ListGymInvoicesQuery(gymId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
