using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Subscriptions.Events;

/// <summary>
/// A gym now has a subscription. Carries the plan and the agreed price, because a consumer acting on
/// this — provisioning entitlements, notifying the owner — should not have to load two aggregates to
/// learn what was agreed.
/// </summary>
public sealed record SubscriptionAssigned(
    Guid SubscriptionId,
    Guid GymId,
    Guid PlanId,
    string BillingCycle,
    string Currency,
    decimal Amount,
    string Status) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.assigned";
}

/// <summary>
/// The gym moved to a different plan. Both plan ids travel, since "what did they move from" is the
/// question any downstream analysis of upgrades and downgrades starts with.
/// </summary>
public sealed record SubscriptionPlanChanged(
    Guid SubscriptionId,
    Guid GymId,
    Guid FromPlanId,
    Guid ToPlanId,
    string BillingCycle,
    string Currency,
    decimal Amount) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.plan_changed";
}

public sealed record SubscriptionTrialConverted(Guid SubscriptionId, Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.trial_converted";
}

public sealed record SubscriptionMarkedPastDue(Guid SubscriptionId, Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.marked_past_due";
}

public sealed record SubscriptionPaymentResumed(Guid SubscriptionId, Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.payment_resumed";
}

/// <summary>
/// Cancellation was *scheduled* — service continues to the end of the paid period. Distinct from
/// <see cref="SubscriptionCanceled"/>, which is the moment it actually ends: a consumer that revoked
/// access on this event would cut off a gym that has already paid for the rest of the month.
/// </summary>
public sealed record SubscriptionCancellationScheduled(
    Guid SubscriptionId,
    Guid GymId,
    DateTimeOffset EffectiveAtUtc) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.cancellation_scheduled";
}

public sealed record SubscriptionCancellationWithdrawn(Guid SubscriptionId, Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.cancellation_withdrawn";
}

public sealed record SubscriptionCanceled(Guid SubscriptionId, Guid GymId, string? Reason) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.canceled";
}

/// <summary>
/// A period ended with no renewal. Deliberately not the same event as a cancellation — see
/// <c>SubscriptionStatusTransitions</c> on why churn needs the two kept apart.
/// </summary>
public sealed record SubscriptionExpired(Guid SubscriptionId, Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.expired";
}

public sealed record SubscriptionRenewed(
    Guid SubscriptionId,
    Guid GymId,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc) : DomainEvent
{
    public override string EventName => "gymos.platform.subscription.renewed";
}
