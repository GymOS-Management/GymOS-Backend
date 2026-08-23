using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Currencies;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>What one renewal-sweep step did to one subscription — the job's log line.</summary>
public enum RenewalAction
{
    /// <summary>Nothing was due — the candidate query over-approximates on purpose.</summary>
    None = 0,

    /// <summary>The trial ended: converted and the first period billed.</summary>
    ConvertedTrial = 1,

    /// <summary>A scheduled cancellation reached the period end and became final.</summary>
    FinalizedCancellation = 2,

    /// <summary>The period ended with auto-renew off: time ran out (not a decision — Expired, not Canceled).</summary>
    Expired = 3,

    /// <summary>The period advanced and the new period's invoice was issued.</summary>
    Renewed = 4,

    /// <summary>A pending plan change applied at the boundary, then the period advanced and billed.</summary>
    AppliedPendingChangeAndRenewed = 5,
}

/// <summary>
/// One subscription's renewal-sweep step (M3.4 design §3) — the four state-derived questions, in
/// the order that keeps them from contradicting each other.
///
/// <para>An Application service like <see cref="InvoiceIssuer"/>, because the step spans
/// aggregates (subscription, plan, invoice, number sequence). The job calls it once per due
/// subscription <b>in its own scope</b>, so each step is its own transaction: a failure isolates
/// to one gym, and the next tick finds its state unchanged and simply tries again — every branch
/// mutates the very condition that selected it, which is what makes the sweep idempotent.</para>
/// </summary>
public sealed class SubscriptionRenewalService
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IPlanRepository _plans;
    private readonly InvoiceIssuer _issuer;
    private readonly IFeatureExceptionMaintainer _featureExceptions;
    private readonly IUnitOfWork _unitOfWork;

    public SubscriptionRenewalService(
        ISubscriptionRepository subscriptions,
        IPlanRepository plans,
        InvoiceIssuer issuer,
        IFeatureExceptionMaintainer featureExceptions,
        IUnitOfWork unitOfWork)
    {
        _subscriptions = subscriptions;
        _plans = plans;
        _issuer = issuer;
        _featureExceptions = featureExceptions;
        _unitOfWork = unitOfWork;
    }

    public async Task<RenewalAction> ProcessAsync(
        Guid subscriptionId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The whole step is one explicit transaction over an UPDLOCK read: a console edit
        // racing the sweep waits its turn instead of winning at save time — by which point the
        // step has drawn a non-refundable invoice number, and every lost race burned one.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);

        GymSubscription? subscription = await _subscriptions.FindByIdForUpdateAsync(
            subscriptionId, cancellationToken);
        if (subscription is null || !subscription.IsLive)
        {
            return RenewalAction.None; // ended between the candidate query and this step
        }

        // A RUNNING trial is never renewed and never billed, however far it outlasts its first
        // period — nothing has been paid, so there is nothing a period boundary can charge for.
        // The period catches up at conversion, below.
        if (subscription.Status == SubscriptionStatus.Trialing
            && !subscription.TrialHasEnded(now))
        {
            return RenewalAction.None;
        }

        RenewalAction action;

        // 1. An expired trial converts and bills — unless the gym already asked to cancel, in
        //    which case converting would bill a customer on their way out; the cancellation
        //    finalizes at the period end below instead. CatchUpPeriods first: a trial longer
        //    than one cycle left lapsed periods behind it, and the first real invoice must cover
        //    the period the gym is IN, not one that expired unbilled mid-trial.
        if (subscription.Status == SubscriptionStatus.Trialing
            && !subscription.CancelAtPeriodEnd)
        {
            SubscriptionPlan plan = await RequirePlanAsync(subscription.PlanId, cancellationToken);
            subscription.ConvertTrial();
            subscription.CatchUpPeriods(now);
            await _issuer.IssueForPeriodAsync(subscription, plan, cancellationToken);
            action = RenewalAction.ConvertedTrial;
        }
        else
        {
            if (!subscription.PeriodHasEnded(now))
            {
                return RenewalAction.None;
            }

            // 2. A scheduled cancellation becomes final — the gym got every day it paid for.
            if (subscription.CancelAtPeriodEnd)
            {
                subscription.CancelNow(reason: null, now); // keeps the reason recorded at scheduling
                action = RenewalAction.FinalizedCancellation;
            }

            // 3. Auto-renew off with no cancellation asked: time ran out. Expired, not Canceled —
            //    the churn figure counts decisions, and nobody decided this.
            else if (!subscription.AutoRenew)
            {
                subscription.Expire(now);
                action = RenewalAction.Expired;
            }
            else
            {
                // 4. Renewal — applying the scheduled plan change first when one has come due, so
                //    the new period starts on the terms the gym chose for it.
                bool changedPlan = false;
                SubscriptionPlan renewalPlan;

                if (subscription.PendingChange is { } pending && pending.ChangeAtUtc <= now)
                {
                    renewalPlan = await RequirePlanAsync(pending.PlanId, cancellationToken);

                    if (!Currency.TryOf(pending.CurrencyCode, out Currency currency))
                    {
                        throw new InvalidOperationException(
                            $"Pending change on subscription '{subscription.Id}' carries unknown " +
                            $"currency '{pending.CurrencyCode}'.");
                    }

                    subscription.ChangePlan(
                        renewalPlan,
                        new SubscriptionTerms(
                            pending.PlanPriceId, pending.BillingCycle, Money.Of(pending.Amount, currency)));
                    changedPlan = true;
                }
                else
                {
                    renewalPlan = await RequirePlanAsync(subscription.PlanId, cancellationToken);
                }

                // The period advances from where it ended (renewing late must not drift the
                // billing date), and the new period bills at the subscription's copied price —
                // the snapshot rule. A PastDue subscription renews too: service is never withheld
                // by billing state, and the old period's invoice stays open until it is settled.
                subscription.Renew();
                await _issuer.IssueForPeriodAsync(subscription, renewalPlan, cancellationToken);
                action = changedPlan ? RenewalAction.AppliedPendingChangeAndRenewed : RenewalAction.Renewed;
            }
        }

        // One call, here, rather than one per branch above: every branch that reaches this point
        // has just finished mutating the subscription — converted, cancelled, expired, renewed, or
        // renewed onto a new plan — and any of those can leave a stored exception no longer
        // differing from what the gym gets anyway. Checking once at this single point, after every
        // mutation and before the one save that commits it, means a future fifth or sixth branch is
        // covered by construction instead of by remembering to add another call inside it — which
        // is exactly the enumeration mistake that left this branch uncovered the first time.
        //
        // Passed the in-memory subscription's own liveness and plan id, not looked up: CancelNow
        // and Expire only set Status in memory, so a query run before the save below would still
        // match the not-yet-saved row as live and hand back the plan that is ending.
        // Read here, used after the commit below: CancelNow and Expire only set Status in memory, so
        // this has to be sampled while the in-memory subscription still describes the change that was
        // just made — a query afterwards would answer about the committed row instead.
        Guid? livePlanIdAfterChange = subscription.IsLive ? subscription.PlanId : null;

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Withdrawn AFTER the billing transaction commits, and this ordering is load-bearing.
        //
        // The withdrawal deletes gym_feature_overrides rows, which carry row-version tokens and sit under no
        // lock, so an operator restating one underneath makes the delete match nothing and raises a
        // concurrency conflict. Staged inside the transaction above, that conflict rolled the whole
        // renewal back — including an invoice number IssueForPeriodAsync had already drawn from a
        // sequence, which does not give numbers back ("a rollback after the draw burns it"). A support
        // agent editing a feature flag could therefore void a renewal and consume its number, and the
        // 15-minute sweep would burn another on every racing retry.
        //
        // Billing must not depend on feature-flag housekeeping. The real guarantee for withdrawals is
        // FeatureOverrideSweepJob's nightly pass, so a failure here is a deferral, not a loss: the
        // sweep re-evaluates this gym and reaches the same verdict.
        // Saved only when there is something to save, which is the overwhelmingly common case for a
        // renewal: most gyms hold no exceptions at all. That keeps this step at one save — the
        // repository's "one SaveChangesAsync per use case" rule — for every renewal that changes no
        // exception, and takes a second one only where housekeeping genuinely has work to do, which is
        // the case that must not share the billing transaction.
        try
        {
            int withdrawn = await _featureExceptions.WithdrawRedundantAsync(
                subscription.GymId, livePlanIdAfterChange, cancellationToken);

            if (withdrawn > 0)
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (ConcurrencyConflictException)
        {
            // Only this one is swallowed, and only because it is the expected interleaving with an
            // operator's own edit — whose decision should win anyway. Anything else propagates to the
            // job, which reports it per gym, because a withdrawal failing for an unexpected reason is
            // worth knowing about even though the renewal itself is already safely committed.
        }

        return action;
    }

    private async Task<SubscriptionPlan> RequirePlanAsync(Guid planId, CancellationToken cancellationToken) =>
        await _plans.FindByIdAsync(planId, cancellationToken)
            ?? throw new InvalidOperationException(
                // Plans are never deleted (M3.1 makes DELETE a 405), so a missing one is a defect
                // worth a loud failure, not a silent skip.
                $"Plan '{planId}' does not exist, but a live subscription references it.");
}
