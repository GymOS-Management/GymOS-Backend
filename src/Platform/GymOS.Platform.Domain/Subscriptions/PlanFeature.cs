using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Subscriptions;

/// <summary>
/// What one plan says about one feature (ARCHITECTURE.md §2.4's <c>plan_features</c>) — the second
/// resolution layer, and the thing the platform actually sells.
///
/// <para>Part of the <see cref="SubscriptionPlan"/> aggregate and created only through it, exactly
/// like <see cref="PlanPrice"/>: "what this plan includes" is a property of the plan, and letting a
/// row be written independently would allow a feature set nobody reviewed as a whole.</para>
///
/// <para><b>A row is an opinion, and its absence is silence.</b> No row means the plan says nothing
/// about the feature, and resolution falls through to the platform default — which is why
/// <see cref="SubscriptionPlan.ClearFeature"/> exists alongside setting it to <c>false</c>. "This
/// plan does not include CRM" and "this plan has no view on CRM" look identical to a gym today, but
/// they answer differently the moment the platform default changes.</para>
/// </summary>
public sealed class PlanFeature : Entity
{
    internal PlanFeature(Guid id, Guid planId, Guid featureId, bool isEnabled)
        : base(id)
    {
        PlanId = planId;
        FeatureId = featureId;
        IsEnabled = isEnabled;
    }

    private PlanFeature()
    {
    }

    public Guid PlanId { get; private set; }

    public Guid FeatureId { get; private set; }

    public bool IsEnabled { get; private set; }

    internal void ChangeTo(bool isEnabled) => IsEnabled = isEnabled;
}
