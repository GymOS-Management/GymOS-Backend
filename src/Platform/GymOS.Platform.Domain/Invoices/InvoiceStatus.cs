using GymOS.SharedKernel;

namespace GymOS.Platform.Domain.Invoices;

/// <summary>
/// Where an invoice stands (ARCHITECTURE.md §2.3). Numeric values are stored and are stable API:
/// renumbering them would reinterpret every invoice ever issued — the one kind of row a business is
/// obliged to be able to reproduce years later.
/// </summary>
public enum InvoiceStatus
{
    /// <summary>
    /// Prepared but not yet issued. <b>Nothing sets this today.</b> Every M3.3 invoice is a consequence
    /// of a subscription event and is issued at the moment it is created; a draft only makes sense once
    /// someone can compose an invoice by hand, which is a later feature.
    /// </summary>
    Draft = 1,

    /// <summary>Sent to the gym and awaiting payment. The state every invoice starts in today.</summary>
    Issued = 2,

    /// <summary>Settled. Terminal — reversing a payment is a credit note, not an edit.</summary>
    Paid = 3,

    /// <summary>
    /// Issued and past its due date, written by the M3.4 overdue job. Still open — an overdue
    /// invoice is paid and voided exactly like an issued one; the status exists so dunning and
    /// reporting have something durable to hang on, while displays keep deriving "overdue right
    /// now" from the due date (<see cref="Invoice.IsOverdue"/>).
    /// </summary>
    Overdue = 4,

    /// <summary>Cancelled before payment. Terminal — the row stays as the record that it was voided.</summary>
    Void = 5,
}

/// <summary>
/// The invoice lifecycle as one explicit transition table, the pattern <c>GymStatusTransitions</c> and
/// <c>SubscriptionStatusTransitions</c> established.
///
/// <code>
///   ┌────────┐    due date passed    ┌─────────┐
///   │ Issued ├──────────────────────►│ Overdue │
///   └─┬────┬─┘                       └──┬────┬─┘
///     │    │ payment recorded / voided  │    │
///     │    └────────────┬───────────────┘    │
///     │                 ▼                    │
///     │             ┌──────┐             ┌──────┐
///     └────────────►│ Paid │ terminal    │ Void │ terminal
///                   └──────┘             └──────┘
/// </code>
///
/// <para><b><see cref="InvoiceStatus.Draft"/> remains deliberately unreachable</b> — nothing produces
/// it until manual invoicing exists, so nothing may leave it either. <see cref="InvoiceStatus.Overdue"/>
/// gained its producer in M3.4 (the overdue job), and with it exactly the moves an open invoice has:
/// payment and voiding.</para>
///
/// <para><b>Both end states are terminal and both exist.</b> Paid means money arrived; Void means the
/// invoice should never have been sent. Correcting a *paid* invoice is a credit note — a separate
/// document with its own number — which is why nothing moves out of <see cref="InvoiceStatus.Paid"/>.
/// </para>
/// </summary>
public static class InvoiceStatusTransitions
{
    private static readonly HashSet<(InvoiceStatus From, InvoiceStatus To)> Allowed =
    [
        (InvoiceStatus.Issued, InvoiceStatus.Paid),
        (InvoiceStatus.Issued, InvoiceStatus.Void),
        (InvoiceStatus.Issued, InvoiceStatus.Overdue),
        (InvoiceStatus.Overdue, InvoiceStatus.Paid),
        (InvoiceStatus.Overdue, InvoiceStatus.Void),
    ];

    /// <summary>Statuses in which an invoice is still awaiting settlement.</summary>
    public static bool IsOpen(InvoiceStatus status) =>
        status is InvoiceStatus.Issued or InvoiceStatus.Overdue;

    public static bool IsAllowed(InvoiceStatus from, InvoiceStatus to) => Allowed.Contains((from, to));

    /// <summary>
    /// Throws when the move is not in the table. An invariant breach rather than a business failure:
    /// callers check the current state first, so reaching this is a defect (house pattern).
    /// </summary>
    public static void EnsureAllowed(InvoiceStatus from, InvoiceStatus to)
    {
        if (from == to)
        {
            throw new DomainException($"The invoice is already {from}.");
        }

        if (!IsAllowed(from, to))
        {
            throw new DomainException($"An invoice cannot move from {from} to {to}.");
        }
    }
}
