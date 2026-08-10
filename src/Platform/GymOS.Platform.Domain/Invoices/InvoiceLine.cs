using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Invoices;

/// <summary>
/// One thing charged for on an invoice (ARCHITECTURE.md §2.3): what it was, and what it cost.
///
/// <para>Part of the <see cref="Invoice"/> aggregate and created only through it, so the invoice can
/// hold the invariants that span its lines — one currency throughout, and a total that is the sum of
/// them.</para>
///
/// <para><b>Lines are never edited.</b> There is no method here that changes one, because an issued
/// invoice is a statement of what was charged: correcting it is a new document, not a rewrite of the
/// one the gym already has.</para>
/// </summary>
public sealed class InvoiceLine : Entity
{
    /// <summary>
    /// The description column's ceiling, held here so the aggregate refuses what the database would
    /// refuse anyway — with a message rather than a truncation error at save time.
    /// </summary>
    public const int MaximumDescriptionLength = 500;

    internal InvoiceLine(Guid id, Guid invoiceId, string description, int quantity, Money unitPrice)
        : base(id)
    {
        InvoiceId = invoiceId;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;

        // Quantity is always 1 today, which the invoice enforces — see Invoice.Issue on why that is a
        // constraint rather than an omission.
        Amount = unitPrice;
    }

    private InvoiceLine()
    {
    }

    public Guid InvoiceId { get; private set; }

    /// <summary>
    /// What the gym is being charged for, in the words it will read: the plan and the period, or the
    /// credit and the days it covers. Composed by the issuer, never by a caller.
    /// </summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>
    /// How many of it. Always 1 in M3.3 — see <see cref="Invoice.Issue"/>.
    /// </summary>
    public int Quantity { get; private set; }

    /// <summary>The price of one, with its currency (AD-42). Never a naked decimal — rule R4.</summary>
    public Money UnitPrice { get; private set; }

    /// <summary>
    /// What this line adds to the invoice. Negative on a credit line, which is how a prorated upgrade
    /// carries the unused time on the old plan.
    /// </summary>
    public Money Amount { get; private set; }
}
