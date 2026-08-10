using GymOS.Platform.Domain.Invoices;

namespace GymOS.Platform.Application.Invoices.Abstractions;

/// <summary>Write-side access to the <see cref="Invoice"/> aggregate (AD-3).</summary>
public interface IInvoiceRepository
{
    Task<Invoice?> FindByIdAsync(Guid invoiceId, CancellationToken cancellationToken);

    void Add(Invoice invoice);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(Invoice invoice, uint expectedVersion);
}
