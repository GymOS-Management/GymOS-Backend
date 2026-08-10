using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The overdue sweep's step: an unpaid due date becomes durable state, the current period going
/// unpaid takes the subscription PastDue with it (the approved auto-PastDue decision), and
/// everything else is left exactly alone.
/// </summary>
public sealed class InvoiceOverdueTests
{
    private static readonly DateTimeOffset IssuedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Context
    {
        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeInvoiceRepository Invoices { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public InvoiceOverdueService Service => new(Invoices, Subscriptions, UnitOfWork);

        public GymSubscription GivenSubscription(bool pastDue = false)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create("PRO", "PRO", "", 0, PlanLimits.Unlimited);
            plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Currency.Euro));
            Plans.Seed(plan);

            GymSubscription subscription = GymSubscription.Assign(
                Guid.NewGuid(), plan, SubscriptionTerms.From(plan.Prices.Single()), IssuedAt);

            if (pastDue)
            {
                subscription.MarkPastDue();
            }

            Subscriptions.Seed(subscription);
            return subscription;
        }

        public Invoice GivenInvoice(
            GymSubscription subscription,
            string number = "2026-000001",
            DateTimeOffset? issuedAt = null)
        {
            DateTimeOffset issued = issuedAt ?? IssuedAt;

            Invoice invoice = Invoice.Issue(
                subscription.GymId,
                subscription.Id,
                number,
                [new InvoiceLineSpec("PRO — one period", 1, Money.Of(49.99m, Currency.Euro))],
                0m,
                issued,
                issued.AddDays(InvoicePaymentTerms.Days));

            Invoices.Seed(invoice);
            return invoice;
        }
    }

    [Fact]
    public async Task AnUnpaidDueDate_BecomesOverdueAndTakesTheActiveSubscriptionPastDue()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice invoice = context.GivenInvoice(subscription);

        OverdueAction action = await context.Service.ProcessAsync(
            invoice.Id, invoice.DueAtUtc.AddHours(2), CancellationToken.None);

        Assert.Equal(OverdueAction.MarkedOverdueAndSubscriptionPastDue, action);
        Assert.Equal(InvoiceStatus.Overdue, invoice.Status);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.Equal(1, context.UnitOfWork.SaveCount); // one transaction covers both
    }

    [Fact]
    public async Task AnInvoiceNotYetDue_IsLeftAlone()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice invoice = context.GivenInvoice(subscription);

        OverdueAction action = await context.Service.ProcessAsync(
            invoice.Id, invoice.DueAtUtc.AddSeconds(-1), CancellationToken.None);

        Assert.Equal(OverdueAction.None, action);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AnOldPeriodsInvoice_MarksOverdueWithoutTouchingTheSubscription()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice oldInvoice = context.GivenInvoice(
            subscription, issuedAt: subscription.CurrentPeriodStartUtc.AddMonths(-2));

        OverdueAction action = await context.Service.ProcessAsync(
            oldInvoice.Id, oldInvoice.DueAtUtc.AddDays(40), CancellationToken.None);

        // An old debt crossing its due date says nothing new about the period the gym is in now.
        Assert.Equal(OverdueAction.MarkedOverdue, action);
        Assert.Equal(InvoiceStatus.Overdue, oldInvoice.Status);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public async Task AnAlreadyPastDueSubscription_IsNotFlippedAgain()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(pastDue: true);
        Invoice invoice = context.GivenInvoice(subscription);

        OverdueAction action = await context.Service.ProcessAsync(
            invoice.Id, invoice.DueAtUtc.AddHours(2), CancellationToken.None);

        // PastDue -> PastDue is not in the transition table, and it has nothing new to say.
        Assert.Equal(OverdueAction.MarkedOverdue, action);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    [Fact]
    public async Task ASettledInvoice_IsSkippedEvenWhenTheCandidateQueryWasStale()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice invoice = context.GivenInvoice(subscription);
        invoice.MarkPaid("paid between query and step", invoice.DueAtUtc.AddHours(1));

        OverdueAction action = await context.Service.ProcessAsync(
            invoice.Id, invoice.DueAtUtc.AddHours(2), CancellationToken.None);

        Assert.Equal(OverdueAction.None, action);
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
    }

    [Fact]
    public async Task RunningTheStepTwice_ChangesNothingTheSecondTime()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice invoice = context.GivenInvoice(subscription);
        DateTimeOffset now = invoice.DueAtUtc.AddHours(2);

        OverdueAction first = await context.Service.ProcessAsync(invoice.Id, now, CancellationToken.None);
        OverdueAction second = await context.Service.ProcessAsync(invoice.Id, now, CancellationToken.None);

        Assert.Equal(OverdueAction.MarkedOverdueAndSubscriptionPastDue, first);
        Assert.Equal(OverdueAction.None, second);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task PayingEverythingOpen_BringsTheSubscriptionBack_TheFullCircle()
    {
        // The whole dunning loop with no operator in it: overdue flips the subscription out,
        // settling the period flips it back (M3.3's settled-period rule).
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription();
        Invoice invoice = context.GivenInvoice(subscription);
        DateTimeOffset pastDue = invoice.DueAtUtc.AddHours(2);

        await context.Service.ProcessAsync(invoice.Id, pastDue, CancellationToken.None);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);

        invoice.MarkPaid("late but honest", pastDue.AddHours(1));
        bool anotherOpen = await context.Invoices.AnyOtherOpenForSubscriptionAsync(
            subscription.Id, subscription.CurrentPeriodStartUtc, invoice.Id, CancellationToken.None);
        Assert.False(anotherOpen); // the settled-period condition mark-paid checks
        subscription.ResumePayment();

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }
}
