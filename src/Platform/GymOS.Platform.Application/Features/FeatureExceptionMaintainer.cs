using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Features;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// Re-evaluates one gym's stored exceptions against its live plan and the platform defaults, and
/// withdraws whichever no longer differ (<see cref="GymFeatureExceptions.Redundant"/>).
///
/// <para>Public, like <see cref="GymOS.Platform.Application.Invoices.InvoiceIssuer"/>, so the
/// Infrastructure composition root can register it by type without needing visibility into this
/// assembly's internals. It follows the same rule <c>InvoiceIssuer</c> does:
/// <b>it only stages changes and never calls <c>SaveChangesAsync</c></b> — the caller's own save
/// commits the withdrawal together with the subscription change that caused it, or not at all.</para>
/// </summary>
public sealed class FeatureExceptionMaintainer : IFeatureExceptionMaintainer
{
    private readonly IFeatureRepository _features;
    private readonly IPlanRepository _plans;
    private readonly ISubscriptionRepository _subscriptions;

    public FeatureExceptionMaintainer(
        IFeatureRepository features,
        IPlanRepository plans,
        ISubscriptionRepository subscriptions)
    {
        _features = features;
        _plans = plans;
        _subscriptions = subscriptions;
    }

    public async Task<int> WithdrawRedundantAsync(Guid gymId, CancellationToken cancellationToken)
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

        Dictionary<Guid, bool> planOpinions = (await PlanFeatureLoader.LoadPlanFeaturesAsync(
                _plans, _subscriptions, gymId, cancellationToken))
            .ToDictionary(planFeature => planFeature.FeatureId, planFeature => planFeature.IsEnabled);

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
}
