using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Invoices;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>In-memory stand-in for the invoice write side.</summary>
internal sealed class FakeInvoiceRepository : IInvoiceRepository
{
    private readonly List<Invoice> _invoices = [];

    public IReadOnlyList<Invoice> Invoices => _invoices;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    public void Seed(Invoice invoice) => _invoices.Add(invoice);

    public Task<Invoice?> FindByIdAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        Task.FromResult(_invoices.SingleOrDefault(invoice => invoice.Id == invoiceId));

    public Task<bool> AnyOtherOpenForSubscriptionAsync(
        Guid subscriptionId,
        DateTimeOffset issuedOnOrAfterUtc,
        Guid excludingInvoiceId,
        CancellationToken cancellationToken) =>
        Task.FromResult(_invoices.Any(invoice =>
            invoice.SubscriptionId == subscriptionId
            && invoice.Id != excludingInvoiceId
            && invoice.IssuedAtUtc >= issuedOnOrAfterUtc
            && InvoiceStatusTransitions.IsOpen(invoice.Status)));

    public void Add(Invoice invoice) => _invoices.Add(invoice);

    public void ExpectVersion(Invoice invoice, uint expectedVersion)
    {
        ExpectedVersions[invoice.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }
}

/// <summary>
/// Counts per year like the real sequence does, so tests can assert literal numbers without a
/// database in the room.
/// </summary>
internal sealed class FakeInvoiceNumberGenerator : IInvoiceNumberGenerator
{
    private readonly Dictionary<int, long> _counters = [];

    public Task<string> NextAsync(int year, CancellationToken cancellationToken)
    {
        long next = _counters.TryGetValue(year, out long current) ? current + 1 : 1;
        _counters[year] = next;
        return Task.FromResult(string.Create(CultureInfo.InvariantCulture, $"{year}-{next:D6}"));
    }
}

/// <summary>
/// Read side, projecting from the same aggregates the write side mutates — so a handler that returns
/// the resource it just changed is checked against real state.
/// </summary>
internal sealed class FakeInvoiceQueries : IInvoiceQueries
{
    private readonly FakeInvoiceRepository _invoices;
    private readonly FakeGymRepository _gyms;
    private readonly IClock _clock;

    public FakeInvoiceQueries(FakeInvoiceRepository invoices, FakeGymRepository gyms, IClock clock)
    {
        _invoices = invoices;
        _gyms = gyms;
        _clock = clock;
    }

    public uint RowVersion { get; set; } = 1;

    public Task<PagedResult<InvoiceListItem>> ListAsync(
        InvoiceListFilter filter,
        CancellationToken cancellationToken)
    {
        IEnumerable<Invoice> invoices = _invoices.Invoices;

        if (filter.Status is { } status)
        {
            InvoiceStatus parsed = Enum.Parse<InvoiceStatus>(status, ignoreCase: true);
            invoices = invoices.Where(invoice => invoice.Status == parsed);
        }

        if (filter.GymId is { } gymId)
        {
            invoices = invoices.Where(invoice => invoice.GymId == gymId);
        }

        if (filter.Overdue is { } overdue)
        {
            invoices = invoices.Where(invoice => invoice.IsOverdue(_clock.UtcNow) == overdue);
        }

        var items = invoices.Select(ProjectListItem).ToList();

        return Task.FromResult(
            new PagedResult<InvoiceListItem>(items, filter.Page.Page, filter.Page.PageSize, items.Count));
    }

    public Task<InvoiceDetail?> GetAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        Invoice? invoice = _invoices.Invoices.SingleOrDefault(candidate => candidate.Id == invoiceId);
        return Task.FromResult(invoice is null ? null : Project(invoice));
    }

    public Task<IReadOnlyList<InvoiceListItem>> ListForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InvoiceListItem>>(
            _invoices.Invoices
                .Where(invoice => invoice.GymId == gymId)
                .OrderByDescending(invoice => invoice.IssuedAtUtc)
                .Select(ProjectListItem)
                .ToList());

    public Task<IReadOnlyList<Guid>> ListNewlyOverdueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>(
            _invoices.Invoices
                .Where(invoice => invoice.Status == InvoiceStatus.Issued && invoice.DueAtUtc <= now)
                .OrderBy(invoice => invoice.DueAtUtc)
                .Select(invoice => invoice.Id)
                .ToList());

    private string GymName(Guid gymId) =>
        _gyms.Gyms.SingleOrDefault(gym => gym.Id == gymId)?.Name ?? string.Empty;

    private InvoiceListItem ProjectListItem(Invoice invoice) => new(
        invoice.Id,
        invoice.GymId,
        GymName(invoice.GymId),
        invoice.InvoiceNumber,
        invoice.Status.ToString(),
        invoice.IsOverdue(_clock.UtcNow),
        invoice.Total.Amount,
        invoice.Total.Currency.Code,
        invoice.IssuedAtUtc,
        invoice.DueAtUtc,
        invoice.PaidAtUtc);

    private InvoiceDetail Project(Invoice invoice) => new(
        invoice.Id,
        invoice.GymId,
        GymName(invoice.GymId),
        invoice.SubscriptionId,
        invoice.InvoiceNumber,
        invoice.Status.ToString(),
        invoice.IsOverdue(_clock.UtcNow),
        invoice.Subtotal.Amount,
        invoice.TaxRate,
        invoice.TaxAmount.Amount,
        invoice.Total.Amount,
        invoice.Total.Currency.Code,
        invoice.IssuedAtUtc,
        invoice.DueAtUtc,
        invoice.PaidAtUtc,
        invoice.PaymentReference,
        invoice.Lines
            .Select(line => new InvoiceLineDetail(
                line.Id,
                line.Description,
                line.Quantity,
                line.UnitPrice.Amount,
                line.Amount.Amount,
                line.Amount.Currency.Code))
            .ToList(),
        invoice.CreatedAtUtc,
        RowVersion.ToString(CultureInfo.InvariantCulture));
}
