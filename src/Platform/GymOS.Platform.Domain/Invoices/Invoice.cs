using GymOS.Platform.Domain.Invoices.Events;
using GymOS.Platform.Domain.Invoices.Rules;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Domain.Invoices;

/// <summary>
/// What a gym owes for a period of its subscription (ARCHITECTURE.md §2.3), and whether it has paid.
///
/// <para>Platform-scope, like <c>Gym</c> and <c>GymSubscription</c> — the gym is the customer here, not
/// the tenant whose data this is, so there is no <c>tenant_id</c> and R14/RLS still has no subject.</para>
///
/// <para><b>Invoices are created as a consequence, never on request.</b> There is no
/// <c>POST /invoices</c>: one is issued when a subscription is assigned without a trial, when a trial
/// converts, and when a plan change costs money — each inside the same transaction as the change that
/// caused it, so a gym can never be charged for something that did not happen.</para>
///
/// <para><b>Nothing here edits an issued invoice.</b> The only two things that can happen to one are
/// recording a payment and voiding it. An invoice is a statement of what was charged; correcting it
/// after the fact is a credit note, which is a document with its own number and its own decision to
/// take (deferred — <c>docs/BusinessRules/Invoices.md</c>).</para>
/// </summary>
public sealed class Invoice : AggregateRoot, IAuditable
{
    private readonly List<InvoiceLine> _lines = [];

    private Invoice(Guid id, Guid gymId, Guid subscriptionId, string invoiceNumber)
        : base(id)
    {
        GymId = gymId;
        SubscriptionId = subscriptionId;
        InvoiceNumber = invoiceNumber;
    }

    private Invoice()
    {
    }

    public Guid GymId { get; private set; }

    public Guid SubscriptionId { get; private set; }

    /// <summary>
    /// The number this invoice is known by, <c>YYYY-NNNNNN</c>, unique and **immutable**.
    ///
    /// It is what the gym quotes when it asks about a charge and what an accountant reconciles
    /// against, so it leaves the system the moment it is issued — the same reasoning that makes a
    /// gym's slug and a plan's code immutable, with more at stake.
    /// </summary>
    public string InvoiceNumber { get; private set; } = string.Empty;

    public InvoiceStatus Status { get; private set; }

    /// <summary>The sum of the lines, before tax (AD-42).</summary>
    public Money Subtotal { get; private set; }

    /// <summary>
    /// The rate applied to <see cref="Subtotal"/>, as a fraction — <c>0.19m</c> is 19%.
    ///
    /// <para>A scalar multiplier rather than an amount, which is why it is a <see cref="decimal"/>
    /// while every figure it produces is <c>Money</c>; rule R4 exempts it by name for that reason.
    /// Stored on the invoice rather than looked up when displayed, because the rate that applied on
    /// the day is a fact about this document — a later change to tax policy must not silently restate
    /// an invoice already sent.</para>
    ///
    /// <para><b>Zero until accounting counsel supplies jurisdiction values.</b> The computation is real
    /// so that switching it on is a configuration change rather than a schema one; the columns hold
    /// honest zeros, never nulls.</para>
    /// </summary>
    public decimal TaxRate { get; private set; }

    /// <summary><see cref="Subtotal"/> × <see cref="TaxRate"/>, rounded to the currency's minor units.</summary>
    public Money TaxAmount { get; private set; }

    /// <summary>What the gym owes: <see cref="Subtotal"/> + <see cref="TaxAmount"/>.</summary>
    public Money Total { get; private set; }

    public DateTimeOffset IssuedAtUtc { get; private set; }

    /// <summary>When payment is due — <see cref="InvoicePaymentTerms.Days"/> after issue.</summary>
    public DateTimeOffset DueAtUtc { get; private set; }

    public DateTimeOffset? PaidAtUtc { get; private set; }

    /// <summary>How the payment was identified. Set with the payment and never afterwards.</summary>
    public string? PaymentReference { get; private set; }

    public IReadOnlyCollection<InvoiceLine> Lines => _lines.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>
    /// Whether payment is late, at the moment asked.
    ///
    /// <para>Derived, never stored. A stored flag needs a job to keep it true, and the night that job
    /// failed it would say an overdue invoice was fine — the same reasoning that keeps a subscription's
    /// expired trial derived. <see cref="InvoiceStatus.Overdue"/> exists for the M3.4 job to write when
    /// there is something to write it *for* (a dunning email, a report); until then this is the answer
    /// every read model uses.</para>
    /// </summary>
    public bool IsOverdue(DateTimeOffset now) => Status == InvoiceStatus.Issued && now >= DueAtUtc;

    /// <summary>
    /// Issues an invoice for the given lines. The only way an <see cref="Invoice"/> comes into
    /// existence, and it comes into existence already issued — see the type's remarks.
    /// </summary>
    /// <param name="taxRate">
    /// A fraction, not a percentage: 19% is <c>0.19m</c>. Zero throughout M3.3 — see
    /// <see cref="TaxRate"/>.
    /// </param>
    /// <exception cref="DomainException">
    /// No lines; lines in more than one currency; a quantity other than 1; a line with no description
    /// or one longer than the column; a negative tax rate; a due date before the issue date; a missing
    /// gym, subscription, or number; or a negative total.
    /// </exception>
    public static Invoice Issue(
        Guid gymId,
        Guid subscriptionId,
        string invoiceNumber,
        IReadOnlyList<InvoiceLineSpec> lines,
        decimal taxRate,
        DateTimeOffset issuedAt,
        DateTimeOffset dueAt)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (gymId == Guid.Empty)
        {
            throw new DomainException("An invoice must be addressed to a gym.");
        }

        if (subscriptionId == Guid.Empty)
        {
            throw new DomainException("An invoice must belong to a subscription.");
        }

        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            throw new DomainException("An invoice must be issued with a number.");
        }

        if (lines.Count == 0)
        {
            throw new DomainException("An invoice must have at least one line.");
        }

        if (taxRate < 0m)
        {
            throw new DomainException($"A tax rate cannot be negative, but was {taxRate}.");
        }

        if (dueAt < issuedAt)
        {
            throw new DomainException("An invoice cannot fall due before it is issued.");
        }

        Currency currency = lines[0].UnitPrice.Currency;

        // Checked up front rather than left to Money's mismatch, so the error names the actual
        // mistake: an invoice is one document settled in one currency, and a caller that mixed two has
        // built the wrong thing rather than added the wrong pair of amounts.
        if (lines.Any(line => line.UnitPrice.Currency != currency))
        {
            throw new DomainException(
                "An invoice is settled in a single currency, but its lines were in more than one.");
        }

        var invoice = new Invoice(Guid.NewGuid(), gymId, subscriptionId, invoiceNumber.Trim())
        {
            Status = InvoiceStatus.Issued,
            TaxRate = taxRate,
            IssuedAtUtc = issuedAt,
            DueAtUtc = dueAt,
        };

        Money subtotal = Money.Zero(currency);

        foreach (InvoiceLineSpec line in lines)
        {
            invoice.AddLine(line);
            subtotal = subtotal.Add(line.UnitPrice);
        }

        invoice.Subtotal = subtotal;

        // Rounded to the currency's minor units, not to two decimals: KWD divides into thousandths and
        // JPY not at all, so a fixed two would charge the wrong amount for both. Half-to-even is the
        // house policy wherever a series of amounts is rounded — it does not drift upward the way
        // half-up does across many invoices.
        invoice.TaxAmount = subtotal.Multiply(taxRate, RoundingPolicy.HalfEven);
        invoice.Total = subtotal.Add(invoice.TaxAmount);

        if (invoice.Total.IsNegative)
        {
            // A net credit is scheduled as a plan change at the end of the period, never issued as a
            // negative invoice — there is no credit-note document to carry one. So a negative total
            // reaching here is the caller having taken a path that was supposed to be unreachable.
            throw new DomainException(
                $"An invoice cannot total less than nothing, but the lines came to {invoice.Total}.");
        }

        invoice.Raise(new InvoiceIssued(
            invoice.Id,
            gymId,
            subscriptionId,
            invoice.InvoiceNumber,
            invoice.Total.Amount,
            currency.Code,
            dueAt));

        return invoice;
    }

    /// <summary>
    /// Records that the invoice was paid.
    ///
    /// <para>The first command in the platform that must not run twice: a retried request would record
    /// a second payment against the same money. That is enforced one layer out by the AD-39 idempotency
    /// framework, not here — this method's job is to refuse the *second* payment, which it does by way
    /// of the transition table.</para>
    /// </summary>
    /// <exception cref="BusinessRuleViolationException">The reference is missing or too long.</exception>
    /// <exception cref="DomainException">The invoice is not awaiting payment.</exception>
    public void MarkPaid(string paymentReference, DateTimeOffset paidAt)
    {
        // Before the transition, so a caller who forgot the reference has changed nothing.
        BusinessRules.Check(new PaymentReferenceMustBePresent(paymentReference));

        TransitionTo(InvoiceStatus.Paid);

        PaidAtUtc = paidAt;
        PaymentReference = paymentReference.Trim();

        Raise(new InvoicePaid(
            Id,
            GymId,
            InvoiceNumber,
            PaymentReference,
            Total.Amount,
            Total.Currency.Code));
    }

    /// <summary>
    /// Cancels an unpaid invoice. The row stays — voided is a state, not a deletion, and "this was
    /// withdrawn" is exactly what the record has to be able to show.
    /// </summary>
    /// <exception cref="DomainException">The invoice was already paid or already void.</exception>
    public void MarkVoid()
    {
        TransitionTo(InvoiceStatus.Void);

        Raise(new InvoiceVoided(Id, GymId, InvoiceNumber, Total.Amount, Total.Currency.Code));
    }

    private void AddLine(InvoiceLineSpec line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(line.Description))
        {
            throw new DomainException("Every invoice line needs a description.");
        }

        if (line.Description.Trim().Length > InvoiceLine.MaximumDescriptionLength)
        {
            throw new DomainException(
                "An invoice line description is at most " +
                $"{InvoiceLine.MaximumDescriptionLength} characters.");
        }

        if (line.Quantity != 1)
        {
            // M3.3 issues one line per thing charged, so a line's amount is its unit price exactly and
            // no rounding decision arises. Refused rather than quietly ignored: whoever first needs a
            // quantity greater than one has to add the multiply-with-an-explicit-policy path
            // deliberately, instead of inheriting an amount that silently undercharges by a factor of
            // the quantity.
            throw new DomainException(
                $"Invoice lines carry a quantity of 1 in this version, but {line.Quantity} was given. " +
                "Charging for several of something needs a rounding policy for the multiplication.");
        }

        _lines.Add(new InvoiceLine(
            Guid.NewGuid(),
            Id,
            line.Description.Trim(),
            line.Quantity,
            line.UnitPrice));
    }

    /// <summary>The only place <see cref="Status"/> is assigned.</summary>
    private void TransitionTo(InvoiceStatus target)
    {
        InvoiceStatusTransitions.EnsureAllowed(Status, target);
        Status = target;
    }
}

/// <summary>
/// A line to put on an invoice, as the caller states it — the aggregate turns each of these into an
/// <see cref="InvoiceLine"/> once it has checked what it can only check across the whole set.
///
/// <para>A separate shape from the entity so an issuer composes an invoice in one call and the
/// aggregate stays the only thing that can create a line, which is what lets it hold "one currency
/// throughout" and "the total is the sum of the lines" as invariants it actually owns.</para>
/// </summary>
/// <param name="Quantity">Always 1 in this version — see <see cref="Invoice.Issue"/>.</param>
public sealed record InvoiceLineSpec(string Description, int Quantity, Money UnitPrice);

/// <summary>
/// How long a gym has to pay.
///
/// <para>A named constant rather than a setting: it is the same for every gym today, and a
/// configurable payment term needs somewhere to configure it (AD-45 settings) plus a decision about
/// what happens to invoices already issued under the old one. Naming it is what makes that a
/// deliberate change later instead of a search for the number 14.</para>
/// </summary>
public static class InvoicePaymentTerms
{
    public const int Days = 14;
}
