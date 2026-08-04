namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// A role assignment. TenantId scopes the assignment: null = platform-wide,
/// otherwise the role applies within that gym only (AD-22). Created exclusively
/// through <see cref="User.AssignRole"/>.
/// </summary>
public sealed class UserRole
{
    internal UserRole(Guid userId, Guid roleId, Guid? tenantId)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        RoleId = roleId;
        TenantId = tenantId;
    }

    private UserRole()
    {
    }

    public Guid Id { get; private set; }

    public Guid UserId { get; private set; }

    public Guid RoleId { get; private set; }

    public Guid? TenantId { get; private set; }
}
