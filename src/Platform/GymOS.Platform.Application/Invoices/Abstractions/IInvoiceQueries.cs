using GymOS.BuildingBlocks.Application.Models;

namespace GymOS.Platform.Application.Invoices.Abstractions;

/// <summary>
/// An invoice as the API reports it (R4: amounts as decimals beside their currency, on a record).
/// </summary>
/// <param name="GymName">
/// Included beside the id for the same reason a plan's code rides on a subscription: the console
/// renders a list of invoices, and "whose is this" should not cost a request per row.
/// </param>
/// <param name="IsOverdue">
/// Derived at read time from the clock, never stored — see <c>Invoice.IsOverdue</c> on why a stored
/// flag would lie the night the job that maintains it fails.
/// </param>
public sealed record InvoiceDetail(
    Guid Id,
    Guid GymId,
    string GymName,
    Guid SubscriptionId,
    string InvoiceNumber,
    string Status,
    bool IsOverdue,
    decimal Subtotal,
    decimal TaxRate,
    decimal TaxAmount,
    decimal Total,
    string Currency,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DueAtUtc,
    DateTimeOffset? PaidAtUtc,
    string? PaymentReference,
    IReadOnlyList<InvoiceLineDetail> Lines,
    DateTimeOffset CreatedAtUtc,
    string RowVersion);

public sealed record InvoiceLineDetail(
    Guid Id,
    string Description,
    int Quantity,
    decimal UnitPriceAmount,
    decimal Amount,
    string Currency);

public sealed record InvoiceListItem(
    Guid Id,
    Guid GymId,
    string GymName,
    string InvoiceNumber,
    string Status,
    bool IsOverdue,
    decimal Total,
    string Currency,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset DueAtUtc,
    DateTimeOffset? PaidAtUtc);

/// <param name="Status">
/// Validated by the query handler before it gets here — an unknown status is a 400 with
/// <c>invoices.status_invalid</c>, never a silently empty page.
/// </param>
public sealed record InvoiceListFilter(PageRequest Page, string? Status, Guid? GymId, bool? Overdue);

/// <summary>Read-side access to invoices (AD-3): projections only, no aggregates.</summary>
public interface IInvoiceQueries
{
    Task<PagedResult<InvoiceListItem>> ListAsync(InvoiceListFilter filter, CancellationToken cancellationToken);

    Task<InvoiceDetail?> GetAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>The gym tab: every invoice ever issued to one gym, newest first, unpaged.</summary>
    Task<IReadOnlyList<InvoiceListItem>> ListForGymAsync(Guid gymId, CancellationToken cancellationToken);
}
