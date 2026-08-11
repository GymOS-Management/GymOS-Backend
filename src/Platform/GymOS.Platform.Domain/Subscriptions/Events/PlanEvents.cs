using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Subscriptions.Events;

public sealed record PlanCreated(Guid PlanId, string Code, string Name) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.created";
}

public sealed record PlanUpdated(Guid PlanId) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.updated";
}

/// <summary>
/// A price was set or changed for one cycle and currency.
///
/// <para>Carries the amount and currency because that is what a consumer needs to act — a listener
/// notifying gyms of a price change should not have to load a plan to learn what changed to what.
/// Existing subscriptions are unaffected: they hold a price snapshot (§2.3), so a price change applies
/// to new assignments and renewals, never retroactively.</para>
/// </summary>
public sealed record PlanPriceSet(
    Guid PlanId,
    Guid PlanPriceId,
    string BillingCycle,
    string Currency,
    decimal Amount) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.price_set";
}

/// <summary>
/// The plan's opinion about a feature changed (§2.4's second resolution layer). Affects every gym
/// on the plan that has no override of its own, which is why it is worth an event.
/// </summary>
public sealed record PlanFeatureSet(Guid PlanId, Guid FeatureId, bool IsEnabled) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.feature_set";
}

/// <summary>
/// The plan no longer has an opinion about a feature: gyms on it fall through to the platform
/// default. Distinct from setting it disabled — see <c>PlanFeature</c>.
/// </summary>
public sealed record PlanFeatureCleared(Guid PlanId, Guid FeatureId) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.feature_cleared";
}

public sealed record PlanPriceRemoved(Guid PlanId, Guid PlanPriceId) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.price_removed";
}

/// <summary>
/// The plan can no longer be newly assigned. Existing subscriptions on it continue — which is why this
/// is "deactivated" and not "deleted": deleting would orphan billing history (§3.4).
/// </summary>
public sealed record PlanDeactivated(Guid PlanId) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.deactivated";
}

public sealed record PlanActivated(Guid PlanId) : DomainEvent
{
    public override string EventName => "gymos.platform.plan.activated";
}
