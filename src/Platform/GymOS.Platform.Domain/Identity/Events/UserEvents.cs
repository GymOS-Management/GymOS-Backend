using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Identity.Events;

public sealed record UserCreated(Guid UserId, string Email) : DomainEvent
{
    public override string EventName => "gymos.platform.user.created";
}

public sealed record UserSuspended(Guid UserId, string Reason) : DomainEvent
{
    public override string EventName => "gymos.platform.user.suspended";
}

public sealed record UserReactivated(Guid UserId) : DomainEvent
{
    public override string EventName => "gymos.platform.user.reactivated";
}

public sealed record UserPasswordChanged(Guid UserId) : DomainEvent
{
    public override string EventName => "gymos.platform.user.password_changed";
}

public sealed record UserLockedOut(Guid UserId, DateTimeOffset LockedOutUntilUtc) : DomainEvent
{
    public override string EventName => "gymos.platform.user.locked_out";
}

public sealed record UserRoleAssigned(Guid UserId, Guid RoleId, Guid? TenantId) : DomainEvent
{
    public override string EventName => "gymos.platform.user.role_assigned";
}

public sealed record UserRoleRevoked(Guid UserId, Guid RoleId, Guid? TenantId) : DomainEvent
{
    public override string EventName => "gymos.platform.user.role_revoked";
}
