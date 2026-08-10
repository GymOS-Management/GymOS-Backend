using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Invoices.Abstractions;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Application.Invoices;

/// <summary>
/// Composes and issues invoices for subscription events — assignment, trial conversion, and the paid
/// side of a plan change.
///
/// <para>An Application-layer service rather than a domain method because issuing spans aggregates:
/// the subscription being billed, the number sequence, and the invoice itself (the
/// <c>SuperAdminGuard</c> precedent). It only <em>adds</em> the invoice to the repository — the caller
/// saves, so the invoice and the subscription change it bills for commit in one transaction, and a gym
/// can never be charged for something that did not happen.</para>
/// </summary>
public sealed class InvoiceIssuer
{
    /// <summary>
    /// Zero until accounting counsel supplies jurisdiction values (flagged in
    /// <c>docs/BusinessRules/Invoices.md</c>). A named constant so switching tax on is a deliberate,
    /// findable change — the computation downstream of it is already real.
    /// </summary>
    public const decimal CurrentTaxRate = 0m;

    private readonly IInvoiceRepository _invoices;
    private readonly IInvoiceNumberGenerator _numbers;
    private readonly IClock _clock;

    public InvoiceIssuer(IInvoiceRepository invoices, IInvoiceNumberGenerator numbers, IClock clock)
    {
        _invoices = invoices;
        _numbers = numbers;
        _clock = clock;
    }

    /// <summary>One line: the plan, for the subscription's current period, at the agreed price.</summary>
    public async Task<Invoice> IssueForPeriodAsync(
        GymSubscription subscription,
        SubscriptionPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(plan);

        string description = string.Create(
            CultureInfo.InvariantCulture,
            $"{plan.Name} — {subscription.CurrentPeriodStartUtc:yyyy-MM-dd} to {subscription.CurrentPeriodEndUtc:yyyy-MM-dd}");

        return await IssueAsync(
            subscription,
            [new InvoiceLineSpec(description, 1, subscription.Price)],
            cancellationToken);
    }

    /// <summary>
    /// Two lines: credit for the unused time on the old plan, charge for the remainder on the new one.
    /// Only called when the net is positive — a net in the gym's favour is scheduled for the period
    /// end instead (the net-sign policy), because there is no credit-note document to carry it.
    /// </summary>
    /// <remarks>
    /// The credit line names no plan, deliberately: the old plan is not loaded on this path, and the
    /// plan ids travel on the existing <c>SubscriptionPlanChanged</c> event for the audit trail.
    /// </remarks>
    public async Task<Invoice> IssueProrationAsync(
        GymSubscription subscription,
        SubscriptionPlan newPlan,
        ProrationResult proration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(newPlan);
        ArgumentNullException.ThrowIfNull(proration);

        int totalDays = proration.DaysUsed + proration.DaysRemaining;

        string creditDescription = string.Create(
            CultureInfo.InvariantCulture,
            $"Credit — unused subscription time ({proration.DaysRemaining} of {totalDays} days)");

        string chargeDescription = string.Create(
            CultureInfo.InvariantCulture,
            $"{newPlan.Name} — remainder of current period ({proration.DaysRemaining} days)");

        Money credit = Money.Of(
            -proration.UnusedOldPlanCredit.Amount, proration.UnusedOldPlanCredit.Currency);

        return await IssueAsync(
            subscription,
            [
                new InvoiceLineSpec(creditDescription, 1, credit),
                new InvoiceLineSpec(chargeDescription, 1, proration.NewPlanRemainderCharge),
            ],
            cancellationToken);
    }

    private async Task<Invoice> IssueAsync(
        GymSubscription subscription,
        IReadOnlyList<InvoiceLineSpec> lines,
        CancellationToken cancellationToken)
    {
        DateTimeOffset issuedAt = _clock.UtcNow;
        string invoiceNumber = await _numbers.NextAsync(issuedAt.Year, cancellationToken);

        Invoice invoice = Invoice.Issue(
            subscription.GymId,
            subscription.Id,
            invoiceNumber,
            lines,
            CurrentTaxRate,
            issuedAt,
            issuedAt.AddDays(InvoicePaymentTerms.Days));

        _invoices.Add(invoice);
        return invoice;
    }
}
