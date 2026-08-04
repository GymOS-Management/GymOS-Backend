namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// A permission grant on a role. Created exclusively through
/// <see cref="Role.GrantPermission"/>.
/// </summary>
public sealed class RolePermission
{
    internal RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    private RolePermission()
    {
    }

    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }
}
