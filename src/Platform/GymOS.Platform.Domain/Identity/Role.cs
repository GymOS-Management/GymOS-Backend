using GymOS.Platform.Domain.Identity.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// A named permission bundle (AD-5/AD-22): roles carry no behavior of their own —
/// authorization always evaluates permissions. Platform and tenant scopes share
/// this one type so gym-defined custom roles (future) need no second mechanism.
/// System roles are seeded and cannot be renamed or deleted.
/// </summary>
public sealed class Role : AggregateRoot, IAuditable
{
    private readonly List<RolePermission> _permissions = [];

    private Role(Guid id, string name, string description, RoleScope scope, Guid? tenantId, bool isSystem)
        : base(id)
    {
        Name = name;
        NormalizedName = Normalize(name);
        Description = description;
        Scope = scope;
        TenantId = tenantId;
        IsSystem = isSystem;
    }

    private Role()
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public RoleScope Scope { get; private set; }

    /// <summary>Null for platform roles; the owning gym for tenant roles (AD-22).</summary>
    public Guid? TenantId { get; private set; }

    /// <summary>Seeded roles: undeletable, unrenamable — their grants stay code-canonical.</summary>
    public bool IsSystem { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static Role Create(string name, string description, RoleScope scope, Guid? tenantId = null, bool isSystem = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (scope == RoleScope.Tenant && tenantId is null)
        {
            throw new DomainException("A tenant-scoped role must belong to a tenant.");
        }

        if (scope == RoleScope.Platform && tenantId is not null)
        {
            throw new DomainException("A platform-scoped role cannot belong to a tenant.");
        }

        var role = new Role(Guid.NewGuid(), name.Trim(), description.Trim(), scope, tenantId, isSystem);
        role.Raise(new RoleCreated(role.Id, role.Name, scope, tenantId));
        return role;
    }

    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim().ToUpperInvariant();
    }

    public void Rename(string name, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (IsSystem)
        {
            throw new DomainException("System roles cannot be renamed.");
        }

        Name = name.Trim();
        NormalizedName = Normalize(name);
        Description = description.Trim();
    }

    /// <summary>Idempotent: granting an already-granted permission is a no-op (no event).</summary>
    public void GrantPermission(Guid permissionId)
    {
        if (_permissions.Any(permission => permission.PermissionId == permissionId))
        {
            return;
        }

        _permissions.Add(new RolePermission(Id, permissionId));
        Raise(new RolePermissionGranted(Id, permissionId));
    }

    public void RevokePermission(Guid permissionId)
    {
        RolePermission? grant = _permissions.FirstOrDefault(permission => permission.PermissionId == permissionId);
        if (grant is null)
        {
            return;
        }

        _permissions.Remove(grant);
        Raise(new RolePermissionRevoked(Id, permissionId));
    }
}
