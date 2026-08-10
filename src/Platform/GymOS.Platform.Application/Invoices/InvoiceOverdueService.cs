using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.Invoices;

/// <summary>What one overdue-sweep step did to one invoice — the job's log line.</summary>
public enum OverdueAction
{
    /// <summary>Nothing was due — settled or already marked between the candidate query and this step.</summary>
    None = 0,

    /// <summary>The invoice became Overdue.</summary>
    MarkedOverdue = 1,

    /// <summary>The invoice became Overdue and its subscription went PastDue with it.</summary>
    MarkedOverdueAndSubscriptionPastDue = 2,
}

/// <summary>
/// One invoice's overdue-sweep step (M3.4 design §4): the due date passed unpaid, so the derived
/// answer becomes a durable state — and, when it is the current period going unpaid, the
/// subscription goes <see cref="SubscriptionStatus.PastDue"/> in the same transaction (the
/// approved auto-PastDue decision; the Golden Rule direction — no operator has to notice).
///
/// <para>Mark-paid's settled-period rule (M3.3) is the return path: paying everything open in the
/// period flips the subscription back, also with no operator involved. Service is never withheld
/// by any of this — PastDue is bookkeeping that suspension, a human decision, may follow.</para>
/// </summary>
public sealed class InvoiceOverdueService
{
    private readonly IInvoiceRepository _invoices;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IUnitOfWork _unitOfWork;

    public InvoiceOverdueService(
        IInvoiceRepository invoices,
        ISubscriptionRepository subscriptions,
        IUnitOfWork unitOfWork)
    {
        _invoices = invoices;
        _subscriptions = subscriptions;
        _unitOfWork = unitOfWork;
    }

    public async Task<OverdueAction> ProcessAsync(
        Guid invoiceId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Invoice? invoice = await _invoices.FindByIdAsync(invoiceId, cancellationToken);
        if (invoice is null
            || invoice.Status != InvoiceStatus.Issued
            || now < invoice.DueAtUtc)
        {
            return OverdueAction.None; // settled, voided, or already marked since the candidate query
        }

        invoice.MarkOverdue(now);

        // The current period going unpaid is what PastDue means; an old invoice crossing its due
        // date says nothing new about the period the gym is in now. Only an Active subscription
        // flips — PastDue already says it, and a trial or ended subscription has no payment state
        // to speak of.
        bool subscriptionFlipped = false;
        GymSubscription? subscription = await _subscriptions.FindByIdAsync(
            invoice.SubscriptionId, cancellationToken);
        if (subscription is not null
            && subscription.Status == SubscriptionStatus.Active
            && invoice.IssuedAtUtc >= subscription.CurrentPeriodStartUtc)
        {
            subscription.MarkPastDue();
            subscriptionFlipped = true;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return subscriptionFlipped
            ? OverdueAction.MarkedOverdueAndSubscriptionPastDue
            : OverdueAction.MarkedOverdue;
    }
}
