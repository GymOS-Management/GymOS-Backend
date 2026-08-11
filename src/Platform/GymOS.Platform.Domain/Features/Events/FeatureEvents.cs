using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Features.Events;

/// <summary>
/// The platform-wide default for a feature changed — re-answering it for every gym that never made
/// a decision of its own, which is why this is worth an event rather than a silent column write.
/// </summary>
public sealed record FeatureDefaultChanged(Guid FeatureId, string Key, bool DefaultEnabled) : DomainEvent
{
    public override string EventName => "gymos.platform.feature.default_changed";
}

/// <summary>
/// A gym was given an exception to what its plan gives. Carries the reason and the author, because
/// those are the two things anyone asking about it later actually wants.
/// </summary>
public sealed record GymFeatureOverridden(
    Guid GymId,
    Guid FeatureId,
    string Key,
    bool IsEnabled,
    string Reason,
    Guid? SetByUserId) : DomainEvent
{
    public override string EventName => "gymos.platform.gym_feature.overridden";
}

/// <summary>
/// A gym's exception was withdrawn: the feature follows its plan again. Raised on the clear path,
/// including when an operator sets a value that happens to equal what the plan already gives.
/// </summary>
public sealed record GymFeatureOverrideCleared(Guid GymId, Guid FeatureId, string Key) : DomainEvent
{
    public override string EventName => "gymos.platform.gym_feature.override_cleared";
}
