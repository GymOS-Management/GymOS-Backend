using GymOS.SharedKernel.Events;

namespace GymOS.Platform.Domain.Identity.Events;

public sealed record RoleCreated(Guid RoleId, string Name, RoleScope Scope, Guid? TenantId) : DomainEvent
{
    public override string EventName => "gymos.platform.role.created";
}

public sealed record RolePermissionGranted(Guid RoleId, Guid PermissionId) : DomainEvent
{
    public override string EventName => "gymos.platform.role.permission_granted";
}

public sealed record RolePermissionRevoked(Guid RoleId, Guid PermissionId) : DomainEvent
{
    public override string EventName => "gymos.platform.role.permission_revoked";
}
