using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Invoices;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core adapter for the <see cref="Invoice"/> aggregate. Lookups include the lines: they are part
/// of the aggregate, and its totals are invariants over them.
/// </summary>
internal sealed class InvoiceRepository : IInvoiceRepository
{
    private readonly AppDbContext _dbContext;

    public InvoiceRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<Invoice?> FindByIdAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        _dbContext.Set<Invoice>()
            .Include(invoice => invoice.Lines)
            .SingleOrDefaultAsync(invoice => invoice.Id == invoiceId, cancellationToken);

    public Task<bool> AnyOtherOpenForSubscriptionAsync(
        Guid subscriptionId,
        DateTimeOffset issuedOnOrAfterUtc,
        Guid excludingInvoiceId,
        CancellationToken cancellationToken) =>
        // The two statuses are InvoiceStatusTransitions.IsOpen, inlined because EF cannot
        // translate a method call — the transition table remains the definition of "open".
        _dbContext.Set<Invoice>().AnyAsync(
            invoice => invoice.SubscriptionId == subscriptionId
                && invoice.Id != excludingInvoiceId
                && invoice.IssuedAtUtc >= issuedOnOrAfterUtc
                && (invoice.Status == InvoiceStatus.Issued || invoice.Status == InvoiceStatus.Overdue),
            cancellationToken);

    public void Add(Invoice invoice) => _dbContext.Set<Invoice>().Add(invoice);

    public void ExpectVersion(Invoice invoice, uint expectedVersion) =>
        _dbContext.Entry(invoice).Property<uint>("xmin").OriginalValue = expectedVersion;
}
