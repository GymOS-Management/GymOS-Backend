using GymOS.Platform.Domain.Invoices;

namespace GymOS.Platform.Application.Invoices.Abstractions;

/// <summary>Write-side access to the <see cref="Invoice"/> aggregate (AD-3).</summary>
public interface IInvoiceRepository
{
    Task<Invoice?> FindByIdAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the subscription has another open (still-payable) invoice issued on or after
    /// <paramref name="issuedOnOrAfterUtc"/>, besides <paramref name="excludingInvoiceId"/>.
    ///
    /// <para>The mark-paid handler's question: does paying <em>this</em> invoice settle the current
    /// period, or does money remain outstanding? A past-due subscription recovers only on the
    /// former — paying a small proration invoice while the period's main invoice is unpaid is not
    /// "the payment arrived".</para>
    /// </summary>
    Task<bool> AnyOtherOpenForSubscriptionAsync(
        Guid subscriptionId,
        DateTimeOffset issuedOnOrAfterUtc,
        Guid excludingInvoiceId,
        CancellationToken cancellationToken);

    void Add(Invoice invoice);

    /// <summary>Declares the version the caller believes they are updating (ARCHITECTURE.md §3).</summary>
    void ExpectVersion(Invoice invoice, uint expectedVersion);
}
