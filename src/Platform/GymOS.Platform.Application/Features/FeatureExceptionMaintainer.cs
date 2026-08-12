using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// Re-evaluates one gym's stored exceptions against its live plan and the platform defaults, and
/// withdraws whichever no longer differ (<see cref="GymFeatureExceptions.Redundant"/>).
///
/// <para>Public, like <see cref="GymOS.Platform.Application.Invoices.InvoiceIssuer"/>, so the
/// unit and integration test suites can construct it directly rather than needing a second seam.
/// (<c>internal</c> would in fact have registered fine from
/// <c>PlatformApplicationServiceCollectionExtensions</c> — an Application-assembly composition
/// root that already registers <c>InvoiceIssuer</c> and <c>SubscriptionRenewalService</c> the same
/// way — but it is registered from the Infrastructure assembly's DI extension instead, which has no
/// <c>InternalsVisibleTo</c> grant onto this one, so <c>public</c> is what that registration needs.)
/// It follows <c>InvoiceIssuer</c>'s other rule too: <b>it only stages changes and never calls
/// <c>SaveChangesAsync</c></b> — the caller's own save commits the withdrawal together with the
/// subscription change that caused it, or not at all. Structurally guaranteed rather than merely
/// promised: this class has no <c>IUnitOfWork</c> to call it on.</para>
///
/// <para><b>Takes the plan that will be live once the caller's transaction commits, not a gym id to
/// look one up.</b> An earlier version queried <c>ISubscriptionRepository.FindLiveByGymAsync</c>
/// itself and was wrong on two of its three call sites: a brand-new assignment is an unsaved
/// <c>Add</c>, invisible to that query; and ending a subscription only sets <c>Status</c> in
/// memory, so the same query still matches the not-yet-saved row and hands back the plan that is
/// ending, not "no live plan". The three callers already hold the post-change truth in memory —
/// <c>subscription.IsLive ? subscription.PlanId : (Guid?)null</c> — so this asks them for it
/// instead of re-deriving a wrong answer from a database row nobody has saved yet.</para>
/// </summary>
public sealed class FeatureExceptionMaintainer : IFeatureExceptionMaintainer
{
    private readonly IFeatureRepository _features;
    private readonly IPlanRepository _plans;

    public FeatureExceptionMaintainer(IFeatureRepository features, IPlanRepository plans)
    {
        _features = features;
        _plans = plans;
    }

    public async Task<int> WithdrawRedundantAsync(
        Guid gymId, Guid? livePlanIdAfterChange, CancellationToken cancellationToken)
    {
        IReadOnlyList<GymFeatureOverride> stored =
            await _features.ListOverridesForGymAsync(gymId, cancellationToken);

        if (stored.Count == 0)
        {
            return 0; // The common case: most gyms hold no exceptions at all.
        }

        IReadOnlyList<Feature> catalogue = await _features.ListAllAsync(cancellationToken);
        Dictionary<Guid, bool> defaults = catalogue.ToDictionary(
            feature => feature.Id, feature => feature.DefaultEnabled);

        Dictionary<Guid, bool> planOpinions = await PlanOpinionsAsync(
            livePlanIdAfterChange, cancellationToken);

        IReadOnlyList<Guid> redundant = GymFeatureExceptions.Redundant(
            stored.Select(over => new GymFeatureExceptions.StoredException(over.FeatureId, over.IsEnabled)),
            planOpinions,
            defaults);

        Dictionary<Guid, string> keys = catalogue.ToDictionary(
            feature => feature.Id, feature => feature.Key);

        foreach (Guid featureId in redundant)
        {
            GymFeatureOverride exception = stored.Single(over => over.FeatureId == featureId);

            // The same two calls the operator-driven clear path makes, so a withdrawal the system
            // decided on is indistinguishable in the audit trail from one an operator asked for —
            // which is correct: it IS the same decision, taken by the rule rather than by a person.
            exception.MarkCleared(keys[featureId]);
            _features.RemoveOverride(exception);
        }

        return redundant.Count;
    }

    /// <summary>
    /// The named plan's feature opinions, by id — looked up directly, since the caller already
    /// resolved <em>which</em> plan (or none) is live and only the features behind that id are
    /// still unknown here. No live plan (a <see langword="null"/> id, or one the catalogue no longer
    /// recognises) means every feature falls through to the platform default, exactly as
    /// <see cref="Abstractions.IFeatureQueries.ResolveForGymAsync"/> treats a gym with no
    /// subscription.
    /// </summary>
    private async Task<Dictionary<Guid, bool>> PlanOpinionsAsync(
        Guid? livePlanIdAfterChange, CancellationToken cancellationToken)
    {
        if (livePlanIdAfterChange is not { } planId)
        {
            return [];
        }

        SubscriptionPlan? plan = await _plans.FindByIdAsync(planId, cancellationToken);

        return plan is null
            ? []
            : plan.Features.ToDictionary(planFeature => planFeature.FeatureId, planFeature => planFeature.IsEnabled);
    }
}
