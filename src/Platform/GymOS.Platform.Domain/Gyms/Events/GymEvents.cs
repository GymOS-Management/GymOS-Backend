using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Gyms.Events;

public sealed record GymProvisioned(Guid GymId, string Slug, string Name) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.provisioned";
}

/// <summary>
/// The gym opened for business. Separate from provisioning because this is the moment a tenant becomes
/// live — the point downstream modules care about, and not necessarily the day the record was created.
/// </summary>
public sealed record GymActivated(Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.activated";
}

public sealed record GymDetailsUpdated(Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.details_updated";
}

public sealed record GymSuspended(Guid GymId, string Reason) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.suspended";
}

public sealed record GymReactivated(Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.reactivated";
}

/// <summary>
/// Soft delete. Named "deleted" because that is what it means to every consumer; the row surviving for
/// the retention window is a storage concern. The purge job raises its own event when the data actually
/// goes.
/// </summary>
public sealed record GymDeleted(Guid GymId, string Slug) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.deleted";
}

public sealed record GymBrandingUpdated(Guid GymId) : DomainEvent
{
    public override string EventName => "gymos.platform.gym.branding_updated";
}
