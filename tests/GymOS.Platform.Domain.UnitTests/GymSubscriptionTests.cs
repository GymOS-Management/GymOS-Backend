using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Domain.Subscriptions.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class GymSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Currency Eur = Currency.Euro;

    private static SubscriptionPlan Plan(int trialDays = 0) => SubscriptionPlan.Create(
        "PRO", "Pro", "", trialDays, PlanLimits.Unlimited);

    private static SubscriptionTerms Monthly(decimal amount = 49.99m) =>
        new(Guid.NewGuid(), BillingCycle.Monthly, Money.Of(amount, Eur));

    private static GymSubscription Assign(int trialDays = 0, SubscriptionTerms? terms = null)
    {
        GymSubscription subscription = GymSubscription.Assign(
            GymId, Plan(trialDays), terms ?? Monthly(), Now);
        subscription.DequeueDomainEvents();
        return subscription;
    }

    // ── Assignment ───────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_WithoutATrial_StartsActive()
    {
        GymSubscription subscription = GymSubscription.Assign(GymId, Plan(), Monthly(), Now);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Null(subscription.TrialEndsAtUtc);
        Assert.True(subscription.IsLive);
        Assert.True(subscription.AutoRenew);

        var assigned = Assert.IsType<SubscriptionAssigned>(Assert.Single(subscription.DomainEvents));
        Assert.Equal("Active", assigned.Status);
        Assert.Equal("EUR", assigned.Currency);
        Assert.Equal(49.99m, assigned.Amount);
    }

    [Fact]
    public void Assign_WithATrial_StartsTrialingAndDatesTheTrialEnd()
    {
        GymSubscription subscription = GymSubscription.Assign(GymId, Plan(trialDays: 14), Monthly(), Now);

        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(Now.AddDays(14), subscription.TrialEndsAtUtc);
    }

    [Fact]
    public void Assign_RunsTheFirstPeriodForOneCycleEvenWithATrial()
    {
        GymSubscription subscription = GymSubscription.Assign(GymId, Plan(trialDays: 14), Monthly(), Now);

        // A trial is a grace on payment, not a pause on the calendar: a monthly plan still renews a
        // month in, which is what the gym's first invoice will say.
        Assert.Equal(Now, subscription.CurrentPeriodStartUtc);
        Assert.Equal(Now.AddMonths(1), subscription.CurrentPeriodEndUtc);
    }

    [Theory]
    [InlineData(BillingCycle.Monthly, 1)]
    [InlineData(BillingCycle.Quarterly, 3)]
    [InlineData(BillingCycle.Yearly, 12)]
    public void Assign_SetsThePeriodFromTheCycle(BillingCycle cycle, int months)
    {
        GymSubscription subscription = GymSubscription.Assign(
            GymId, Plan(), new SubscriptionTerms(null, cycle, Money.Of(10m, Eur)), Now);

        Assert.Equal(Now.AddMonths(months), subscription.CurrentPeriodEndUtc);
    }

    [Fact]
    public void Assign_CopiesThePriceRatherThanReferencingIt()
    {
        SubscriptionPlan plan = Plan();
        PlanPrice price = plan.SetPrice(BillingCycle.Monthly, Money.Of(49.99m, Eur));

        GymSubscription subscription = GymSubscription.Assign(
            GymId, plan, SubscriptionTerms.From(price), Now);

        // The plan is repriced afterwards. The subscription must not follow — §2.3's "renewals honor the
        // price at assignment". An FK alone could not give this, because SetPrice mutates the row.
        plan.SetPrice(BillingCycle.Monthly, Money.Of(79.99m, Eur));

        Assert.Equal(49.99m, subscription.Price.Amount);
        Assert.Equal(price.Id, subscription.PlanPriceId);
    }

    [Fact]
    public void Assign_RefusesAGymlessSubscription() =>
        Assert.ThrowsAny<DomainException>(() =>
            GymSubscription.Assign(Guid.Empty, Plan(), Monthly(), Now));

    // ── Changing plan ────────────────────────────────────────────────────────────

    [Fact]
    public void ChangePlan_UpdatesTermsWithoutEndingTheSubscription()
    {
        GymSubscription subscription = Assign();
        DateTimeOffset periodEnd = subscription.CurrentPeriodEndUtc;

        SubscriptionPlan premium = SubscriptionPlan.Create("PREMIUM", "Premium", "", 0, PlanLimits.Unlimited);
        subscription.ChangePlan(premium, new SubscriptionTerms(null, BillingCycle.Yearly, Money.Of(499m, Eur)));

        // Not a cancellation: the enum has no "replaced" state, and recording an upgrade as churn would
        // make the M5 churn figure wrong.
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal(premium.Id, subscription.PlanId);
        Assert.Equal(499m, subscription.Price.Amount);
        Assert.Equal(BillingCycle.Yearly, subscription.BillingCycle);

        // The paid period is untouched — proration is M3.3's business.
        Assert.Equal(periodEnd, subscription.CurrentPeriodEndUtc);

        var changed = Assert.IsType<SubscriptionPlanChanged>(Assert.Single(subscription.DomainEvents));
        Assert.Equal(premium.Id, changed.ToPlanId);
        Assert.NotEqual(changed.FromPlanId, changed.ToPlanId);
    }

    [Fact]
    public void ChangePlan_IsRefusedOnAnEndedSubscription()
    {
        GymSubscription subscription = Assign();
        subscription.CancelNow("Done", Now);

        Assert.ThrowsAny<DomainException>(() =>
            subscription.ChangePlan(Plan(), Monthly()));
    }

    // ── Trial and payment state ──────────────────────────────────────────────────

    [Fact]
    public void ConvertTrial_MovesToActive()
    {
        GymSubscription subscription = Assign(trialDays: 14);

        subscription.ConvertTrial();

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.IsType<SubscriptionTrialConverted>(Assert.Single(subscription.DomainEvents));
    }

    [Fact]
    public void ConvertTrial_IsRefusedWhenNotTrialing() =>
        Assert.ThrowsAny<DomainException>(() => Assign().ConvertTrial());

    [Fact]
    public void TrialHasEnded_IsDerivedAndDoesNotChangeTheStatus()
    {
        GymSubscription subscription = Assign(trialDays: 14);

        Assert.False(subscription.TrialHasEnded(Now));
        Assert.True(subscription.TrialHasEnded(Now.AddDays(15)));

        // Derived, so no job is needed to keep it true — and deliberately *not* a status change:
        // converting is an explicit act until the renewal job lands (M3.4).
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
    }

    [Fact]
    public void MarkPastDue_DoesNotEndTheSubscription()
    {
        GymSubscription subscription = Assign();

        subscription.MarkPastDue();

        // Still live: whether a late-paying gym keeps operating is a separate decision, taken by
        // suspending the gym. Every failed card must not end a business's day.
        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
        Assert.True(subscription.IsLive);
        Assert.IsType<SubscriptionMarkedPastDue>(Assert.Single(subscription.DomainEvents));
    }

    [Fact]
    public void ResumePayment_ReturnsAPastDueSubscriptionToActive()
    {
        GymSubscription subscription = Assign();
        subscription.MarkPastDue();
        subscription.DequeueDomainEvents();

        subscription.ResumePayment();

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.IsType<SubscriptionPaymentResumed>(Assert.Single(subscription.DomainEvents));
    }

    [Fact]
    public void ATrialCanGoStraightPastDue()
    {
        // The first charge after a trial can fail like any other.
        GymSubscription subscription = Assign(trialDays: 14);

        subscription.MarkPastDue();

        Assert.Equal(SubscriptionStatus.PastDue, subscription.Status);
    }

    // ── Cancellation ─────────────────────────────────────────────────────────────

    [Fact]
    public void ScheduleCancellation_KeepsServiceUntilThePeriodEnds()
    {
        GymSubscription subscription = Assign();

        subscription.ScheduleCancellation("Too expensive");

        // Still active and still live: the gym paid to the end of the period. Treating "asked to
        // cancel" as "cancelled" would cut off a paying customer.
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsLive);
        Assert.True(subscription.CancelAtPeriodEnd);
        Assert.Null(subscription.CanceledAtUtc);
        Assert.Equal("Too expensive", subscription.CancellationReason);

        var scheduled = Assert.IsType<SubscriptionCancellationScheduled>(
            Assert.Single(subscription.DomainEvents));
        Assert.Equal(subscription.CurrentPeriodEndUtc, scheduled.EffectiveAtUtc);
    }

    [Fact]
    public void ScheduleCancellation_IsRefusedTwice()
    {
        GymSubscription subscription = Assign();
        subscription.ScheduleCancellation(null);

        Assert.ThrowsAny<DomainException>(() => subscription.ScheduleCancellation(null));
    }

    [Fact]
    public void WithdrawCancellation_ClearsTheSchedule()
    {
        GymSubscription subscription = Assign();
        subscription.ScheduleCancellation("Mistake");
        subscription.DequeueDomainEvents();

        subscription.WithdrawCancellation();

        Assert.False(subscription.CancelAtPeriodEnd);
        Assert.Null(subscription.CancellationReason);
        Assert.IsType<SubscriptionCancellationWithdrawn>(Assert.Single(subscription.DomainEvents));
    }

    [Fact]
    public void WithdrawCancellation_IsRefusedWhenNoneIsScheduled() =>
        Assert.ThrowsAny<DomainException>(() => Assign().WithdrawCancellation());

    [Fact]
    public void CancelNow_EndsItAndStopsAutoRenewal()
    {
        GymSubscription subscription = Assign();

        subscription.CancelNow("Gym closed", Now);

        Assert.Equal(SubscriptionStatus.Canceled, subscription.Status);
        Assert.False(subscription.IsLive);
        Assert.Equal(Now, subscription.CanceledAtUtc);
        Assert.False(subscription.AutoRenew);
        Assert.False(subscription.CancelAtPeriodEnd);

        var canceled = Assert.IsType<SubscriptionCanceled>(Assert.Single(subscription.DomainEvents));
        Assert.Equal("Gym closed", canceled.Reason);
    }

    [Fact]
    public void CancellationIsTerminal()
    {
        GymSubscription subscription = Assign();
        subscription.CancelNow(null, Now);

        // A gym that returns gets a new subscription, leaving this one as the record of what it had.
        Assert.ThrowsAny<DomainException>(() => subscription.CancelNow(null, Now));
        Assert.ThrowsAny<DomainException>(() => subscription.ScheduleCancellation(null));
        Assert.ThrowsAny<DomainException>(() => subscription.Renew());
        Assert.ThrowsAny<DomainException>(() => subscription.Expire(new DateTimeOffset(2026, 3, 9, 4, 15, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Expire_IsDistinctFromCancellation()
    {
        GymSubscription subscription = Assign();

        subscription.Expire(new DateTimeOffset(2026, 3, 9, 4, 15, 0, TimeSpan.Zero));

        // Two terminal states on purpose: cancellation is a decision (churn), expiry is time running
        // out (usually a failure to collect). Collapsing them would make churn unanswerable.
        Assert.Equal(SubscriptionStatus.Expired, subscription.Status);
        Assert.IsType<SubscriptionExpired>(Assert.Single(subscription.DomainEvents));
    }

    // ── Renewal ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Renew_StartsTheNextPeriodWhereTheLastEnded()
    {
        GymSubscription subscription = Assign();
        DateTimeOffset firstEnd = subscription.CurrentPeriodEndUtc;

        subscription.Renew();

        // From the old end, not from "now": renewing a day late must not shorten the gym's month or
        // drift its billing date forward every cycle.
        Assert.Equal(firstEnd, subscription.CurrentPeriodStartUtc);
        Assert.Equal(firstEnd.AddMonths(1), subscription.CurrentPeriodEndUtc);

        var renewed = Assert.IsType<SubscriptionRenewed>(Assert.Single(subscription.DomainEvents));
        Assert.Equal(firstEnd, renewed.PeriodStartUtc);
    }

    [Fact]
    public void Renew_KeepsTheAgreedPrice()
    {
        GymSubscription subscription = Assign(terms: Monthly(49.99m));

        subscription.Renew();

        Assert.Equal(49.99m, subscription.Price.Amount);
    }

    [Fact]
    public void PeriodHasEnded_IsDerived()
    {
        GymSubscription subscription = Assign();

        Assert.False(subscription.PeriodHasEnded(Now));
        Assert.True(subscription.PeriodHasEnded(Now.AddMonths(1)));
    }

    [Fact]
    public void Expire_StampsWhenItHappened_LikeCancellationDoes()
    {
        // Expiry had no timestamp of its own, and the analytics compiler therefore counted it by
        // CurrentPeriodEndUtc — a business date Expire() never touches. Any expiry applied after its
        // own day had already been compiled landed on NO day at all: the period-end day's row was
        // sealed while the subscription was still live, and the day the expiry actually happened
        // looks for a period end it will not find. Because stats rows are write-once, no re-run
        // recovers it.
        //
        // CancelNow has always stamped CanceledAtUtc at the transition. This is the same fact about
        // the other terminal state.
        GymSubscription subscription = Assign();
        var expiredAt = new DateTimeOffset(2026, 3, 9, 4, 15, 0, TimeSpan.Zero);

        subscription.Expire(expiredAt);

        Assert.Equal(expiredAt, subscription.ExpiredAtUtc);
    }

}
