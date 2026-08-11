using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Subscriptions;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// Loads the gym's live plan's feature opinions — the one place this question is answered, so the
/// write path (<see cref="SetGymFeaturesCommand"/>) and the maintenance path
/// (<see cref="IFeatureExceptionMaintainer"/>) can never drift apart on what "the gym's live plan
/// opinions" means. A second copy of this method was exactly the kind of quiet divergence that let a
/// redundant override survive a plan change undetected.
/// </summary>
internal static class PlanFeatureLoader
{
    /// <summary>
    /// The feature opinions of the gym's live plan, or none when it has no live subscription — in
    /// which case every feature resolves against the platform default, which is what
    /// <see cref="IFeatureQueries.ResolveForGymAsync"/> does too. The two must agree, or a write
    /// would decide "no override needed" against one rule and be read back under another.
    /// </summary>
    public static async Task<IReadOnlyList<PlanFeature>> LoadPlanFeaturesAsync(
        IPlanRepository plans,
        ISubscriptionRepository subscriptions,
        Guid gymId,
        CancellationToken cancellationToken)
    {
        GymSubscription? subscription = await subscriptions.FindLiveByGymAsync(gymId, cancellationToken);
        if (subscription is null)
        {
            return [];
        }

        SubscriptionPlan? plan = await plans.FindByIdAsync(subscription.PlanId, cancellationToken);

        return plan is null ? [] : [.. plan.Features];
    }
}
