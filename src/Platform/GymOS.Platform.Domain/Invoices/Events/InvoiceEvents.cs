using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Invoices.Events;

/// <summary>
/// An invoice was issued to a gym. Carries the total and the due date, so a consumer sending it on —
/// an email, an accounting export — does not have to load the aggregate to say what is owed and by
/// when.
///
/// <para>Money travels as an amount beside its ISO code rather than as a <c>Money</c>: the type has no
/// wire form (rule R4's serialisation-boundary case), and a payload that dropped the currency would be
/// a number nobody could interpret.</para>
/// </summary>
public sealed record InvoiceIssued(
    Guid InvoiceId,
    Guid GymId,
    Guid SubscriptionId,
    string InvoiceNumber,
    decimal Total,
    string Currency,
    DateTimeOffset DueAtUtc) : DomainEvent
{
    public override string EventName => "gymos.platform.invoice.issued";
}

/// <summary>
/// Payment was recorded against an invoice. The reference travels because the consumer most likely to
/// act on this — a reconciliation export — is matching it against the payment provider's own record.
/// </summary>
public sealed record InvoicePaid(
    Guid InvoiceId,
    Guid GymId,
    string InvoiceNumber,
    string PaymentReference,
    decimal Total,
    string Currency) : DomainEvent
{
    public override string EventName => "gymos.platform.invoice.paid";
}

/// <summary>
/// An invoice was cancelled before payment. Distinct from a credit note, which reverses one already
/// paid and is a document in its own right (deferred — see <c>docs/BusinessRules/Invoices.md</c>).
/// </summary>
public sealed record InvoiceVoided(
    Guid InvoiceId,
    Guid GymId,
    string InvoiceNumber,
    decimal Total,
    string Currency) : DomainEvent
{
    public override string EventName => "gymos.platform.invoice.voided";
}
