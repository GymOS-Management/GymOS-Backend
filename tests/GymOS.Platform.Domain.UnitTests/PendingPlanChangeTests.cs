using GymOS.Platform.Domain.Subscriptions;
using GymOS.Platform.Domain.Subscriptions.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

/// <summary>
/// A downgrade is not applied when it is asked for — the gym has paid to the end of the period, so the
/// change waits there. Until the M3.4 renewal job applies it, the subscription carries it as visible,
/// replaceable state: the M3.2 trial-expiry choice again, legible rather than wrong.
/// </summary>
public sealed class PendingPlanChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid GymId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Currency Eur = Currency.Euro;

    private static SubscriptionPlan Plan(string code = "PRO", int trialDays = 0) =>
        SubscriptionPlan.Create(code, code, "", trialDays, PlanLimits.Unlimited);

    private static SubscriptionTerms Terms(decimal amount, BillingCycle cycle = BillingCycle.Monthly) =>
        new(Guid.NewGuid(), cycle, Money.Of(amount, Eur));

    private static GymSubscription Assigned(decimal amount = 49.99m)
    {
        GymSubscription subscription = GymSubscription.Assign(GymId, Plan(), Terms(amount), Now);
        subscription.DequeueDomainEvents();
        return subscription;
    }

    [Fact]
    public void ANewSubscriptionHasNothingPending() =>
        Assert.Null(Assigned().PendingChange);

    [Fact]
    public void SchedulingRecordsTheAgreedTermsAndWhenTheyTakeEffect()
    {
        GymSubscription subscription = Assigned();
        SubscriptionPlan starter = Plan("STARTER");
        SubscriptionTerms terms = Terms(19.99m);

        subscription.SchedulePlanChange(starter, terms, subscription.CurrentPeriodEndUtc);

        PendingPlanChange pending = Assert.IsType<PendingPlanChange>(subscription.PendingChange);
        Assert.Equal(starter.Id, pending.PlanId);
        Assert.Equal(terms.PlanPriceId, pending.PlanPriceId);
        Assert.Equal(BillingCycle.Monthly, pending.BillingCycle);
        Assert.Equal(19.99m, pending.Amount);
        Assert.Equal("EUR", pending.CurrencyCode);
        Assert.Equal(subscription.CurrentPeriodEndUtc, pending.ChangeAtUtc);
    }

    [Fact]
    public void SchedulingRaisesTheEvent()
    {
        GymSubscription subscription = Assigned();
        Guid fromPlanId = subscription.PlanId;
        SubscriptionPlan starter = Plan("STARTER");

        subscription.SchedulePlanChange(
            starter, Terms(19.99m, BillingCycle.Yearly), subscription.CurrentPeriodEndUtc);

        var scheduled = Assert.IsType<PlanChangeScheduled>(Assert.Single(subscription.DomainEvents));
        Assert.Equal(subscription.Id, scheduled.SubscriptionId);
        Assert.Equal(GymId, scheduled.GymId);
        Assert.Equal(fromPlanId, scheduled.FromPlanId);
        Assert.Equal(starter.Id, scheduled.ToPlanId);
        Assert.Equal("Yearly", scheduled.BillingCycle);
        Assert.Equal(19.99m, scheduled.Amount);
        Assert.Equal("EUR", scheduled.Currency);
        Assert.Equal(subscription.CurrentPeriodEndUtc, scheduled.ChangeAtUtc);
    }

    [Fact]
    public void SchedulingChangesNothingAboutWhatTheGymHasToday()
    {
        GymSubscription subscription = Assigned(49.99m);
        Guid planId = subscription.PlanId;
        DateTimeOffset periodEnd = subscription.CurrentPeriodEndUtc;

        subscription.SchedulePlanChange(Plan("STARTER"), Terms(19.99m), periodEnd);

        // The gym paid for this period on these terms. A downgrade that took effect on request would
        // take away service already bought — the same mistake as treating "asked to cancel" as
        // "cancelled".
        Assert.Equal(planId, subscription.PlanId);
        Assert.Equal(Money.Of(49.99m, Eur), subscription.Price);
        Assert.Equal(periodEnd, subscription.CurrentPeriodEndUtc);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public void SchedulingAgainReplacesTheOneAlreadyPending()
    {
        GymSubscription subscription = Assigned();
        SubscriptionPlan starter = Plan("STARTER");
        SubscriptionPlan basic = Plan("BASIC");

        subscription.SchedulePlanChange(starter, Terms(19.99m), subscription.CurrentPeriodEndUtc);
        subscription.SchedulePlanChange(basic, Terms(9.99m), subscription.CurrentPeriodEndUtc);

        // One pending change, latest wins. A queue would mean a gym could stack changes nobody can
        // reason about, and the renewal job would have to decide which one it meant.
        PendingPlanChange pending = Assert.IsType<PendingPlanChange>(subscription.PendingChange);
        Assert.Equal(basic.Id, pending.PlanId);
        Assert.Equal(9.99m, pending.Amount);
        Assert.Equal(2, subscription.DomainEvents.Count);
    }

    [Fact]
    public void CancellingClearsThePendingChange()
    {
        GymSubscription subscription = Assigned();
        subscription.SchedulePlanChange(Plan("STARTER"), Terms(19.99m), subscription.CurrentPeriodEndUtc);
        subscription.DequeueDomainEvents();

        subscription.CancelPendingPlanChange();

        Assert.Null(subscription.PendingChange);
    }

    [Fact]
    public void CancellingWhenNothingIsPendingDoesNothing()
    {
        GymSubscription subscription = Assigned();

        // A no-op rather than a refusal: the caller's intent is "this subscription should have no
        // pending change", and that is already true. The handler is what decides whether the *request*
        // made sense.
        subscription.CancelPendingPlanChange();

        Assert.Null(subscription.PendingChange);
        Assert.Empty(subscription.DomainEvents);
    }

    [Fact]
    public void APendingChangeSurvivesOtherChangesToTheSubscription()
    {
        GymSubscription subscription = Assigned();
        SubscriptionPlan starter = Plan("STARTER");
        subscription.SchedulePlanChange(starter, Terms(19.99m), subscription.CurrentPeriodEndUtc);

        subscription.MarkPastDue();
        subscription.ResumePayment();

        // A failed card does not withdraw a scheduled downgrade.
        Assert.Equal(starter.Id, Assert.IsType<PendingPlanChange>(subscription.PendingChange).PlanId);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Expired)]
    public void AnEndedSubscriptionCannotScheduleAChange(SubscriptionStatus terminal)
    {
        GymSubscription subscription = Assigned();

        if (terminal == SubscriptionStatus.Canceled)
        {
            subscription.CancelNow("done", Now);
        }
        else
        {
            subscription.Expire();
        }

        Assert.Throws<DomainException>(() => subscription.SchedulePlanChange(
            Plan("STARTER"), Terms(19.99m), subscription.CurrentPeriodEndUtc));
    }

    [Fact]
    public void ATrialingSubscriptionCanScheduleAChange()
    {
        GymSubscription subscription = GymSubscription.Assign(
            GymId, Plan(trialDays: 14), Terms(49.99m), Now);
        subscription.DequeueDomainEvents();

        subscription.SchedulePlanChange(
            Plan("STARTER"), Terms(19.99m), subscription.CurrentPeriodEndUtc);

        Assert.NotNull(subscription.PendingChange);
    }
}
