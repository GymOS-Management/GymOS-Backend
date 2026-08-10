using GymOS.Platform.Domain.Subscriptions.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// One gym's subscription to a plan (ARCHITECTURE.md §2.3): what it agreed to pay, for what period, and
/// where that stands.
///
/// <para>Platform-scope, like <c>Gym</c> and <c>SubscriptionPlan</c> — this is the platform's record of a
/// commercial relationship, not tenant data.</para>
///
/// <para><b>One live subscription per gym</b>, enforced by a partial unique index over the live statuses.
/// Ended subscriptions stay: they are the record of what a gym actually had, and deleting them would
/// erase the history invoices and churn figures are built from.</para>
/// </summary>
public sealed class GymSubscription : AggregateRoot, IAuditable
{
    private GymSubscription(Guid id, Guid gymId, Guid planId, SubscriptionTerms terms)
        : base(id)
    {
        GymId = gymId;
        PlanId = planId;
        PlanPriceId = terms.PlanPriceId;
        BillingCycle = terms.BillingCycle;
        Price = terms.Price;
    }

    private GymSubscription()
    {
    }

    public Guid GymId { get; private set; }

    public Guid PlanId { get; private set; }

    /// <summary>
    /// Which plan-price row this was agreed from — provenance only, and **nullable**.
    ///
    /// <para>§2.3 calls this the "price snapshot FK", but an FK cannot be the snapshot here. The same
    /// section makes <c>(plan_id, billing_cycle, currency)</c> unique, which leaves no room for price
    /// versioning, so changing a plan's price *mutates that row*. A subscription that only pointed at it
    /// would silently be repriced — the exact opposite of "renewals honor the price at assignment". And a
    /// withdrawn price would dangle the reference.</para>
    ///
    /// <para>So the agreed terms are **copied** into <see cref="Price"/> and
    /// <see cref="BillingCycle"/> below, and this stays as a pointer for answering "which listed price did
    /// this come from", cleared if that row is removed.</para>
    /// </summary>
    public Guid? PlanPriceId { get; private set; }

    /// <summary>The agreed cycle, copied at assignment. See <see cref="PlanPriceId"/>.</summary>
    public BillingCycle BillingCycle { get; private set; }

    /// <summary>The agreed price, copied at assignment (AD-42). Immune to later plan repricing.</summary>
    public Money Price { get; private set; }

    public SubscriptionStatus Status { get; private set; }

    public DateTimeOffset CurrentPeriodStartUtc { get; private set; }

    public DateTimeOffset CurrentPeriodEndUtc { get; private set; }

    /// <summary>
    /// The date the billing cycle counts from — assignment, or the boundary where the cycle last
    /// changed. Stored because it cannot be derived back: <c>AddMonths</c> clamps a Jan 31 into a
    /// Feb 28, and a period chain computed end-over-end would inherit the clamp forever, billing
    /// a month-end signup on the 28th for the rest of its life. <see cref="Renew"/> restores the
    /// anchor's day-of-month whenever the target month has room for it.
    /// </summary>
    public DateTimeOffset PeriodAnchorUtc { get; private set; }

    /// <summary>When the trial ends. Null when the plan had none.</summary>
    public DateTimeOffset? TrialEndsAtUtc { get; private set; }

    public DateTimeOffset? CanceledAtUtc { get; private set; }

    /// <summary>
    /// A cancellation is scheduled for the end of the paid period.
    ///
    /// Separate from <see cref="CanceledAtUtc"/> because the gym has paid to the end of the period and
    /// keeps service until then. Treating "asked to cancel" as "cancelled" would cut off a paying
    /// customer, which is the most expensive kind of bug in billing.
    /// </summary>
    public bool CancelAtPeriodEnd { get; private set; }

    public bool AutoRenew { get; private set; } = true;

    public string? CancellationReason { get; private set; }

    /// <summary>
    /// A plan change agreed but not yet applied — <see langword="null"/> when there is none.
    ///
    /// <para>At most one. A queue of scheduled changes would leave the renewal job deciding which one
    /// the gym meant, so a second request replaces the first: latest wins, and the audit trail keeps
    /// the ones it replaced.</para>
    /// </summary>
    public PendingPlanChange? PendingChange { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>Whether this subscription still entitles the gym to service.</summary>
    public bool IsLive => SubscriptionStatusTransitions.IsLive(Status);

    /// <summary>
    /// Whether the trial has run out. Derived, not stored — a stored flag needs a job to keep true, and
    /// the night the job failed it would lie. **Note this does not change <see cref="Status"/>**:
    /// converting a trial is an explicit act, automated by the renewal job in M3.4.
    /// </summary>
    public bool TrialHasEnded(DateTimeOffset now) =>
        TrialEndsAtUtc is { } endsAt && now >= endsAt;

    public bool PeriodHasEnded(DateTimeOffset now) => now >= CurrentPeriodEndUtc;

    /// <summary>
    /// Starts a subscription. Begins in <see cref="SubscriptionStatus.Trialing"/> when the plan offers a
    /// trial, otherwise <see cref="SubscriptionStatus.Active"/>.
    ///
    /// <para>The first period runs for one billing cycle from <paramref name="now"/> whether or not there
    /// is a trial: a trial is a grace on *payment*, not a pause on the calendar, so a monthly plan with a
    /// 14-day trial still renews a month in — which is what the gym's first invoice will say.</para>
    /// </summary>
    public static GymSubscription Assign(
        Guid gymId,
        SubscriptionPlan plan,
        SubscriptionTerms terms,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(terms);

        if (gymId == Guid.Empty)
        {
            throw new DomainException("A subscription must belong to a gym.");
        }

        var subscription = new GymSubscription(Guid.NewGuid(), gymId, plan.Id, terms)
        {
            PeriodAnchorUtc = now,
            CurrentPeriodStartUtc = now,
            CurrentPeriodEndUtc = now.AddMonths(terms.BillingCycle.Months()),
            Status = plan.TrialDays > 0 ? SubscriptionStatus.Trialing : SubscriptionStatus.Active,
            TrialEndsAtUtc = plan.TrialDays > 0 ? now.AddDays(plan.TrialDays) : null,
        };

        subscription.Raise(new SubscriptionAssigned(
            subscription.Id,
            gymId,
            plan.Id,
            terms.BillingCycle.ToString(),
            terms.Price.Currency.Code,
            terms.Price.Amount,
            subscription.Status.ToString()));

        return subscription;
    }

    /// <summary>
    /// Moves the gym to a different plan, or to different terms on the same one.
    ///
    /// <para><b>Updates this subscription rather than ending it and starting another.</b> The status enum
    /// has no state for "replaced", so recording a plan change as a cancellation would count it as churn
    /// — and a platform that reports every upgrade as a lost customer is worse than one that reports
    /// nothing. The change itself is in the audit trail, and the event carries both plan ids.</para>
    ///
    /// <para>The period is left alone: the gym has paid to the end of it. Charging or crediting the
    /// difference is proration, which needs an invoice to land on and arrives with M3.3.</para>
    /// </summary>
    public void ChangePlan(SubscriptionPlan plan, SubscriptionTerms terms)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(terms);
        EnsureLive();

        Guid fromPlanId = PlanId;

        // A change applied NOW supersedes any change scheduled for LATER. Leaving the schedule in
        // place would let the renewal job silently revert this change at the period end — a gym
        // that paid a prorated upgrade would find itself moved back to the plan it left.
        PendingChange = null;

        // A new cycle counts from the boundary where it takes effect (cycle changes always land
        // at a period boundary — the handler schedules cross-cycle changes there), so the anchor
        // moves with it; a same-cycle change keeps the original billing day.
        if (BillingCycle != terms.BillingCycle)
        {
            PeriodAnchorUtc = CurrentPeriodEndUtc;
        }

        PlanId = plan.Id;
        PlanPriceId = terms.PlanPriceId;
        BillingCycle = terms.BillingCycle;
        Price = terms.Price;

        Raise(new SubscriptionPlanChanged(
            Id,
            GymId,
            fromPlanId,
            plan.Id,
            terms.BillingCycle.ToString(),
            terms.Price.Currency.Code,
            terms.Price.Amount));
    }

    /// <summary>
    /// Agrees a plan change that takes effect at <paramref name="changeAtUtc"/> — the end of the paid
    /// period — instead of now. What a downgrade becomes; see <see cref="PendingPlanChange"/> for why.
    ///
    /// <para>Nothing about the subscription as it stands today changes: the gym keeps the plan, the
    /// price, and the period it paid for. Replaces any change already pending.</para>
    /// </summary>
    public void SchedulePlanChange(
        SubscriptionPlan plan,
        SubscriptionTerms terms,
        DateTimeOffset changeAtUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(terms);
        EnsureLive();

        PendingChange = new PendingPlanChange(
            plan.Id,
            terms.PlanPriceId,
            terms.BillingCycle,
            terms.Price.Amount,
            terms.Price.Currency.Code,
            changeAtUtc);

        Raise(new PlanChangeScheduled(
            Id,
            GymId,
            PlanId,
            plan.Id,
            terms.BillingCycle.ToString(),
            terms.Price.Amount,
            terms.Price.Currency.Code,
            changeAtUtc));
    }

    /// <summary>
    /// Drops a scheduled plan change, leaving the subscription on the plan it already has.
    ///
    /// <para>A no-op when there is nothing pending, and no event either way. The caller's intent is
    /// "this subscription should have no pending change", and nothing downstream acted on the schedule
    /// in the first place — unlike a withdrawn cancellation, which consumers may already have reacted
    /// to. Whether the *request* made sense is the handler's question, not this one's.</para>
    /// </summary>
    public void CancelPendingPlanChange() => PendingChange = null;

    /// <summary>The trial ended and the gym is now paying.</summary>
    public void ConvertTrial()
    {
        if (Status != SubscriptionStatus.Trialing)
        {
            throw new DomainException("Only a trialing subscription can be converted.");
        }

        TransitionTo(SubscriptionStatus.Active);
        Raise(new SubscriptionTrialConverted(Id, GymId));
    }

    /// <summary>
    /// Payment is late.
    ///
    /// Service is not withheld here: whether a late-paying gym keeps operating is a separate decision,
    /// taken by suspending the gym. Conflating them would mean every failed card ended a business's day.
    /// </summary>
    public void MarkPastDue()
    {
        TransitionTo(SubscriptionStatus.PastDue);
        Raise(new SubscriptionMarkedPastDue(Id, GymId));
    }

    public void ResumePayment()
    {
        if (Status != SubscriptionStatus.PastDue)
        {
            throw new DomainException("Only a past-due subscription can resume.");
        }

        TransitionTo(SubscriptionStatus.Active);
        Raise(new SubscriptionPaymentResumed(Id, GymId));
    }

    /// <summary>
    /// Schedules cancellation for the end of the paid period. Service continues until then.
    /// </summary>
    public void ScheduleCancellation(string? reason)
    {
        EnsureLive();

        if (CancelAtPeriodEnd)
        {
            throw new DomainException("Cancellation is already scheduled.");
        }

        CancelAtPeriodEnd = true;
        CancellationReason = Normalize(reason);

        Raise(new SubscriptionCancellationScheduled(Id, GymId, CurrentPeriodEndUtc));
    }

    /// <summary>Undoes a scheduled cancellation — the gym changed its mind before the period ended.</summary>
    public void WithdrawCancellation()
    {
        EnsureLive();

        if (!CancelAtPeriodEnd)
        {
            throw new DomainException("No cancellation is scheduled.");
        }

        CancelAtPeriodEnd = false;
        CancellationReason = null;

        Raise(new SubscriptionCancellationWithdrawn(Id, GymId));
    }

    /// <summary>
    /// Ends the subscription now, forfeiting the rest of the paid period. Terminal.
    /// </summary>
    public void CancelNow(string? reason, DateTimeOffset now)
    {
        TransitionTo(SubscriptionStatus.Canceled);
        CanceledAtUtc = now;
        CancelAtPeriodEnd = false;
        AutoRenew = false;
        CancellationReason = Normalize(reason) ?? CancellationReason;

        Raise(new SubscriptionCanceled(Id, GymId, CancellationReason));
    }

    /// <summary>
    /// The period ended and nothing renewed it. Terminal, and deliberately not a cancellation — see
    /// <see cref="SubscriptionStatusTransitions"/>.
    /// </summary>
    public void Expire()
    {
        TransitionTo(SubscriptionStatus.Expired);
        Raise(new SubscriptionExpired(Id, GymId));
    }

    /// <summary>
    /// Advances to the next period at the agreed price. Called by the renewal job (M3.4).
    ///
    /// <para>The new period starts where the old one ended, not at "now": renewing a day late must not
    /// shorten the gym's month or drift its billing date forward every cycle.</para>
    /// </summary>
    public void Renew()
    {
        EnsureLive();

        if (!AutoRenew)
        {
            throw new DomainException("This subscription does not auto-renew.");
        }

        CurrentPeriodStartUtc = CurrentPeriodEndUtc;
        CurrentPeriodEndUtc = NextAnchoredPeriodEnd(CurrentPeriodStartUtc);

        Raise(new SubscriptionRenewed(Id, GymId, CurrentPeriodStartUtc, CurrentPeriodEndUtc));
    }

    /// <summary>
    /// Advances the period until it contains <paramref name="now"/> — the catch-up a trial longer
    /// than one billing cycle needs at conversion, so the first invoice covers the period the gym
    /// is actually in rather than one that lapsed, unbilled, while the trial ran. One renewal
    /// event per period crossed; a no-op when the period is already current.
    /// </summary>
    public void CatchUpPeriods(DateTimeOffset now)
    {
        while (AutoRenew && PeriodHasEnded(now))
        {
            Renew();
        }
    }

    /// <summary>
    /// One cycle after <paramref name="from"/>, with the anchor's day-of-month restored whenever
    /// the target month has room — see <see cref="PeriodAnchorUtc"/> for why plain
    /// <c>AddMonths</c> chaining is not this.
    /// </summary>
    private DateTimeOffset NextAnchoredPeriodEnd(DateTimeOffset from)
    {
        DateTimeOffset shifted = from.AddMonths(BillingCycle.Months());
        int day = Math.Min(PeriodAnchorUtc.Day, DateTime.DaysInMonth(shifted.Year, shifted.Month));

        return new DateTimeOffset(shifted.Year, shifted.Month, day, 0, 0, 0, TimeSpan.Zero)
            .Add(PeriodAnchorUtc.TimeOfDay);
    }

    /// <summary>
    /// Turns off auto-renewal: the subscription runs its paid period out and then
    /// <see cref="Expire"/>s instead of renewing (the M3.4 sweep's third question).
    ///
    /// <para>No endpoint calls this yet — cancellation is the gesture the console offers today,
    /// and it communicates more (a reason, an explicit decision). This exists because §2.3's
    /// <c>auto_renew</c> column and the sweep's expiry branch are one policy, and the AD-45
    /// settings surface will want the lever. No event, like
    /// <see cref="CancelPendingPlanChange"/>: nothing downstream acts before the period ends, and
    /// the audit trail records the column change.</para>
    /// </summary>
    public void DisableAutoRenew()
    {
        EnsureLive();
        AutoRenew = false;
    }

    /// <summary>Clears the provenance pointer when the plan price it named is withdrawn.</summary>
    public void ForgetPlanPrice() => PlanPriceId = null;

    private void EnsureLive()
    {
        if (!IsLive)
        {
            throw new DomainException($"A {Status} subscription cannot be changed.");
        }
    }

    /// <summary>The only place <see cref="Status"/> is assigned.</summary>
    private void TransitionTo(SubscriptionStatus target)
    {
        SubscriptionStatusTransitions.EnsureAllowed(Status, target);
        Status = target;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The terms a subscription is agreed on: which listed price it came from, and the cycle and amount
/// copied from it.
///
/// <para>Grouped so the copy and its provenance travel together — passing a price id without the amount
/// it stood for is how a "snapshot" quietly becomes a live reference.</para>
/// </summary>
public sealed record SubscriptionTerms(Guid? PlanPriceId, BillingCycle BillingCycle, Money Price)
{
    /// <summary>Terms taken from a plan's listed price, which is the normal path.</summary>
    public static SubscriptionTerms From(PlanPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);
        return new SubscriptionTerms(price.Id, price.BillingCycle, price.Price);
    }
}
