using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Invoices;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;
using GymOS.TestingSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// The renewal sweep, one state-derived question at a time (M3.4 design §3) — and the property the
/// whole job leans on: running a step twice changes nothing the second time, because every branch
/// mutates the very condition that selected it.
/// </summary>
public sealed class SubscriptionRenewalTests
{
    private static readonly DateTimeOffset AssignedAt = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class Context
    {
        public FakeClock Clock { get; } = new(AssignedAt);

        public FakePlanRepository Plans { get; } = new();

        public FakeSubscriptionRepository Subscriptions { get; } = new();

        public FakeInvoiceRepository Invoices { get; } = new();

        public FakeUnitOfWork UnitOfWork { get; } = new();

        public FakeFeatureExceptionMaintainer FeatureExceptions { get; } = new();

        public SubscriptionRenewalService Service => new(
            Subscriptions,
            Plans,
            new InvoiceIssuer(Invoices, new FakeInvoiceNumberGenerator(), Clock),
            FeatureExceptions,
            UnitOfWork,
            NullLogger<SubscriptionRenewalService>.Instance);

        public SubscriptionPlan GivenPlan(string code = "PRO", int trialDays = 0, decimal amount = 49.99m)
        {
            SubscriptionPlan plan = SubscriptionPlan.Create(code, code, "", trialDays, PlanLimits.Unlimited);
            plan.SetPrice(BillingCycle.Monthly, Money.Of(amount, Currency.Euro));
            Plans.Seed(plan);
            return plan;
        }

        public GymSubscription GivenSubscription(SubscriptionPlan plan)
        {
            GymSubscription subscription = GymSubscription.Assign(
                GymId, plan, SubscriptionTerms.From(plan.Prices.Single()), AssignedAt);
            Subscriptions.Seed(subscription);
            return subscription;
        }
    }

    [Fact]
    public async Task ASubscriptionStillInsideItsPeriod_IsLeftAlone()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan());
        context.Clock.Advance(TimeSpan.FromDays(10));

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.None, action);
        Assert.Empty(context.Invoices.Invoices);
        Assert.Equal(0, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AnEndedPeriod_RenewsFromWhereItEndedAndBillsTheNewPeriod()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan(amount: 49.99m));
        DateTimeOffset firstPeriodEnd = subscription.CurrentPeriodEndUtc;
        context.Clock.Set(firstPeriodEnd.AddHours(3)); // the sweep runs a little late, as it will

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.Renewed, action);

        // The new period starts where the old one ENDED, not at "now" — renewing late must not
        // drift the billing date forward every cycle.
        Assert.Equal(firstPeriodEnd, subscription.CurrentPeriodStartUtc);
        Assert.Equal(firstPeriodEnd.AddMonths(1), subscription.CurrentPeriodEndUtc);

        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        Assert.Equal(Money.Of(49.99m, Currency.Euro), invoice.Total);
        Assert.Contains("2026-09-01", Assert.Single(invoice.Lines).Description); // the NEW period
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task RenewalHonorsTheCopiedPrice_NotThePlansCurrentOne()
    {
        var context = new Context();
        SubscriptionPlan plan = context.GivenPlan(amount: 49.99m);
        GymSubscription subscription = context.GivenSubscription(plan);
        plan.SetPrice(BillingCycle.Monthly, Money.Of(79.99m, Currency.Euro)); // repriced later
        context.Clock.Set(subscription.CurrentPeriodEndUtc);

        await context.Service.ProcessAsync(subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // The snapshot rule, §2.3: renewals honor the price at assignment.
        Assert.Equal(Money.Of(49.99m, Currency.Euro), Assert.Single(context.Invoices.Invoices).Total);
    }

    [Fact]
    public async Task AnExpiredTrial_ConvertsAndBillsTheFirstPeriod()
    {
        var context = new Context();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 14, amount: 29.99m);
        GymSubscription subscription = context.GivenSubscription(plan);
        context.Clock.Advance(TimeSpan.FromDays(14));

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.ConvertedTrial, action);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(Money.Of(29.99m, Currency.Euro), Assert.Single(context.Invoices.Invoices).Total);
    }

    [Fact]
    public async Task ATrialOutlastingItsFirstPeriod_IsNeverBilledMidTrial()
    {
        var context = new Context();
        SubscriptionPlan plan = context.GivenPlan(trialDays: 45, amount: 29.99m);
        GymSubscription subscription = context.GivenSubscription(plan);

        // Day 32: the first monthly period has ended, but the 45-day trial is still running.
        // The old decision tree fell through to renewal here and billed a gym mid-free-trial —
        // then billed the same period AGAIN at conversion.
        context.Clock.Advance(TimeSpan.FromDays(32));
        RenewalAction midTrial = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.None, midTrial);
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Empty(context.Invoices.Invoices);

        // Day 46: the trial ends. Conversion catches the period chain up and bills EXACTLY ONE
        // invoice — for the period the gym is actually in.
        context.Clock.Advance(TimeSpan.FromDays(14));
        RenewalAction converted = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.ConvertedTrial, converted);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.False(subscription.PeriodHasEnded(context.Clock.UtcNow));
        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        Assert.Contains("2026-09-01", Assert.Single(invoice.Lines).Description); // caught-up period
    }

    [Fact]
    public async Task AnExpiredTrialWithACancellationScheduled_IsNotConvertedOrBilled()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan(trialDays: 14));
        subscription.ScheduleCancellation("changed our mind during the trial");
        context.Clock.Advance(TimeSpan.FromDays(14));

        RenewalAction midTrial = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // Converting would bill a customer on their way out. The trial runs quietly to the period
        // end, where the cancellation finalizes.
        Assert.Equal(RenewalAction.None, midTrial);
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Empty(context.Invoices.Invoices);

        context.Clock.Set(subscription.CurrentPeriodEndUtc);
        RenewalAction atPeriodEnd = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.FinalizedCancellation, atPeriodEnd);
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Equal("changed our mind during the trial", subscription.CancellationReason);
        Assert.Empty(context.Invoices.Invoices);
    }

    [Fact]
    public async Task AScheduledCancellation_FinalizesAtThePeriodEndWithoutBilling()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan());
        subscription.ScheduleCancellation("too expensive");
        context.Clock.Set(subscription.CurrentPeriodEndUtc.AddMinutes(20));

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.FinalizedCancellation, action);
        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.Equal("too expensive", subscription.CancellationReason); // recorded at scheduling, kept
        Assert.Empty(context.Invoices.Invoices);
    }

    [Fact]
    public async Task APendingPlanChange_AppliesAtTheBoundaryAndTheNewPeriodBillsTheNewTerms()
    {
        var context = new Context();
        SubscriptionPlan pro = context.GivenPlan("PRO", amount: 49.99m);
        SubscriptionPlan basic = context.GivenPlan("BASIC", amount: 19.99m);
        GymSubscription subscription = context.GivenSubscription(pro);
        subscription.SchedulePlanChange(
            basic, SubscriptionTerms.From(basic.Prices.Single()), subscription.CurrentPeriodEndUtc);
        context.Clock.Set(subscription.CurrentPeriodEndUtc);

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        Assert.Equal(RenewalAction.AppliedPendingChangeAndRenewed, action);
        Assert.Equal(basic.Id, subscription.PlanId);
        Assert.Null(subscription.PendingChange);

        Invoice invoice = Assert.Single(context.Invoices.Invoices);
        Assert.Equal(Money.Of(19.99m, Currency.Euro), invoice.Total); // the downgrade the gym scheduled
        Assert.Contains("BASIC", Assert.Single(invoice.Lines).Description);
    }

    [Fact]
    public async Task AutoRenewOff_ExpiresRatherThanCancels()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan());
        subscription.DisableAutoRenew();
        context.Clock.Set(subscription.CurrentPeriodEndUtc);

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // Time ran out; nobody decided anything. Expired and Canceled are different churn stories.
        Assert.Equal(RenewalAction.Expired, action);
        Assert.Equal(SubscriptionStatus.Expired, subscription.Status);
        Assert.Empty(context.Invoices.Invoices);
    }

    [Fact]
    public async Task APastDueSubscription_StillRenewsAndBills()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan());
        subscription.MarkPastDue();
        context.Clock.Set(subscription.CurrentPeriodEndUtc);

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // Service is never withheld by billing state: the period advances and the new period is
        // billed while the old debt stays open. Recovery still requires settling everything open.
        Assert.Equal(RenewalAction.Renewed, action);
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.Single(context.Invoices.Invoices);
    }

    [Fact]
    public async Task RunningTheStepTwice_ChangesNothingTheSecondTime()
    {
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan());
        context.Clock.Set(subscription.CurrentPeriodEndUtc.AddHours(1));

        RenewalAction first = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);
        RenewalAction second = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // The renewal moved the period end past "now", so the second pass finds nothing due —
        // the property that makes a crashed sweep safe to simply run again.
        Assert.Equal(RenewalAction.Renewed, first);
        Assert.Equal(RenewalAction.None, second);
        Assert.Single(context.Invoices.Invoices);
        Assert.Equal(1, context.UnitOfWork.SaveCount);
    }

    [Fact]
    public async Task AFailedExceptionWithdrawal_DoesNotUndoTheRenewalOrBurnItsInvoiceNumber()
    {
        // The withdrawal used to be staged INSIDE the billing transaction, and it is the one write in
        // that transaction that can fail for reasons having nothing to do with billing: the override
        // rows it deletes carry xmin tokens and are under no lock, so an operator restating one
        // underneath makes the delete match nothing and the whole transaction rolls back.
        //
        // What rolls back with it is not recoverable. IssueForPeriodAsync has already drawn an invoice
        // number from a sequence, and a sequence does not give numbers back — the issuer's own docs say
        // "a rollback after the draw burns it". So a support agent editing a feature flag at 03:00
        // could void a renewal and consume its invoice number, and the 15-minute sweep would burn
        // another on every racing retry.
        //
        // The withdrawal now runs after the commit. It is housekeeping whose real guarantee is the
        // nightly FeatureOverrideSweepJob; the billing write must not depend on it.
        var context = new Context();
        GymSubscription subscription = context.GivenSubscription(context.GivenPlan(amount: 49.99m));
        context.Clock.Set(subscription.CurrentPeriodEndUtc.AddHours(3));

        context.FeatureExceptions.Throws = new ConcurrencyConflictException(
            new InvalidOperationException("an operator restated the override underneath"));

        RenewalAction action = await context.Service.ProcessAsync(
            subscription.Id, context.Clock.UtcNow, CancellationToken.None);

        // The renewal stands, and so does its invoice.
        Assert.Equal(RenewalAction.Renewed, action);
        Assert.Single(context.Invoices.Invoices);

        // And the withdrawal really was attempted, so this is not passing because the call was dropped.
        Assert.Contains(subscription.GymId, context.FeatureExceptions.CalledForGymIds);
    }

}
