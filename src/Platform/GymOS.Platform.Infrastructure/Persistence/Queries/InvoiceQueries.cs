using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using Microsoft.EntityFrameworkCore;
using RowVersion = GymOS.BuildingBlocks.Application.Abstractions.RowVersion;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class InvoiceQueries : IInvoiceQueries
{
    /// <summary>Sortable columns, by request value — an allowlist, for the reason in <c>GymQueries</c>.</summary>
    private static readonly string[] SortableFields =
        ["invoiceNumber", "issuedAtUtc", "dueAtUtc", "total", "status"];

    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;

    public InvoiceQueries(AppDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<PagedResult<InvoiceListItem>> ListAsync(
        InvoiceListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // Read once, so every row in the page answers "overdue?" about the same instant — a page
        // where two rows disagree about what time it is would be a strange document.
        DateTimeOffset now = _clock.UtcNow;

        IQueryable<Invoice> query = _dbContext.Set<Invoice>().AsNoTracking();

        // The handler validated the value; parsing again here keeps this class safe to call from a
        // future caller that did not.
        if (filter.Status is { } status && Enum.TryParse(status, ignoreCase: true, out InvoiceStatus parsed))
        {
            query = query.Where(invoice => invoice.Status == parsed);
        }

        if (filter.GymId is { } gymId)
        {
            query = query.Where(invoice => invoice.GymId == gymId);
        }

        if (filter.Overdue is { } overdue)
        {
            query = overdue
                ? query.Where(invoice => invoice.Status == InvoiceStatus.Issued && invoice.DueAtUtc < now)
                : query.Where(invoice => invoice.Status != InvoiceStatus.Issued || invoice.DueAtUtc >= now);
        }

        if (filter.Page.Search is { } search)
        {
            query = query.Where(invoice => EF.Functions.ILike(invoice.InvoiceNumber, $"%{search}%"));
        }

        int totalCount = await query.CountAsync(cancellationToken);

        var page = await Sort(query, filter.Page)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.GymId,
                invoice.InvoiceNumber,
                invoice.Status,
                Total = invoice.Total.Amount,
                Currency = invoice.Total.Currency,
                invoice.IssuedAtUtc,
                invoice.DueAtUtc,
                invoice.PaidAtUtc,
                GymName = _dbContext.Set<Gym>()
                    .Where(gym => gym.Id == invoice.GymId)
                    .Select(gym => gym.Name)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(row => new InvoiceListItem(
                row.Id,
                row.GymId,
                row.GymName ?? string.Empty,
                row.InvoiceNumber,
                row.Status.ToString(),
                row.Status == InvoiceStatus.Issued && row.DueAtUtc < now,
                row.Total,
                row.Currency.Code,
                row.IssuedAtUtc,
                row.DueAtUtc,
                row.PaidAtUtc))
            .ToList();

        return new PagedResult<InvoiceListItem>(items, filter.Page.Page, filter.Page.PageSize, totalCount);
    }

    public async Task<InvoiceDetail?> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;

        var projection = await _dbContext.Set<Invoice>()
            .AsNoTracking()
            .Where(invoice => invoice.Id == invoiceId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.GymId,
                invoice.SubscriptionId,
                invoice.InvoiceNumber,
                invoice.Status,
                Subtotal = invoice.Subtotal.Amount,
                invoice.TaxRate,
                TaxAmount = invoice.TaxAmount.Amount,
                Total = invoice.Total.Amount,
                Currency = invoice.Total.Currency,
                invoice.IssuedAtUtc,
                invoice.DueAtUtc,
                invoice.PaidAtUtc,
                invoice.PaymentReference,
                invoice.CreatedAtUtc,
                RowVersion = EF.Property<uint>(invoice, "xmin"),
                GymName = _dbContext.Set<Gym>()
                    .Where(gym => gym.Id == invoice.GymId)
                    .Select(gym => gym.Name)
                    .FirstOrDefault(),
                Lines = invoice.Lines
                    .Select(line => new
                    {
                        line.Id,
                        line.Description,
                        line.Quantity,
                        UnitPrice = line.UnitPrice.Amount,
                        Amount = line.Amount.Amount,
                        Currency = line.Amount.Currency,
                    })
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (projection is null)
        {
            return null;
        }

        var lines = projection.Lines
            // Charges before credits. The table has no insertion-order column (an invoice's lines
            // are written once and never edited), so display order is a projection decision — and
            // an invoice reads charge-then-credit.
            .OrderByDescending(line => line.Amount)
            .ThenBy(line => line.Description, StringComparer.Ordinal)
            .Select(line => new InvoiceLineDetail(
                line.Id,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.Amount,
                line.Currency.Code))
            .ToList();

        return new InvoiceDetail(
            projection.Id,
            projection.GymId,
            projection.GymName ?? string.Empty,
            projection.SubscriptionId,
            projection.InvoiceNumber,
            projection.Status.ToString(),
            projection.Status == InvoiceStatus.Issued && projection.DueAtUtc < now,
            projection.Subtotal,
            projection.TaxRate,
            projection.TaxAmount,
            projection.Total,
            projection.Currency.Code,
            projection.IssuedAtUtc,
            projection.DueAtUtc,
            projection.PaidAtUtc,
            projection.PaymentReference,
            lines,
            projection.CreatedAtUtc,
            RowVersion.ToToken(projection.RowVersion));
    }

    public async Task<IReadOnlyList<InvoiceListItem>> ListForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        PagedResult<InvoiceListItem> page = await ListAsync(
            new InvoiceListFilter(
                new PageRequest(1, PageRequest.MaxPageSize, "issuedAtUtc", "desc", null),
                Status: null,
                GymId: gymId,
                Overdue: null),
            cancellationToken);

        return page.Items;
    }

    private static IQueryable<Invoice> Sort(IQueryable<Invoice> query, PageRequest page) =>
        (page.SortBy is not null && SortableFields.Contains(page.SortBy, StringComparer.OrdinalIgnoreCase)
            ? page.SortBy.ToLowerInvariant() switch
            {
                "invoicenumber" => page.Descending
                    ? query.OrderByDescending(invoice => invoice.InvoiceNumber)
                    : query.OrderBy(invoice => invoice.InvoiceNumber),
                "dueatutc" => page.Descending
                    ? query.OrderByDescending(invoice => invoice.DueAtUtc)
                    : query.OrderBy(invoice => invoice.DueAtUtc),
                "total" => page.Descending
                    ? query.OrderByDescending(invoice => invoice.Total.Amount)
                    : query.OrderBy(invoice => invoice.Total.Amount),
                "status" => page.Descending
                    ? query.OrderByDescending(invoice => invoice.Status)
                    : query.OrderBy(invoice => invoice.Status),
                _ => page.Descending
                    ? query.OrderByDescending(invoice => invoice.IssuedAtUtc)
                    : query.OrderBy(invoice => invoice.IssuedAtUtc),
            }
            // Newest first: the invoice someone is looking for is almost always the latest one.
            : query.OrderByDescending(invoice => invoice.IssuedAtUtc))
        .ThenBy(invoice => invoice.Id);
}
