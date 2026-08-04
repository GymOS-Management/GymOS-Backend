using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Identity.Events;
using GymOS.SharedKernel;
using Xunit;

namespace GymOS.Platform.Domain.UnitTests;

public sealed class RoleTests
{
    [Fact]
    public void Create_EnforcesScopeTenantConsistency()
    {
        Assert.Throws<DomainException>(() => Role.Create("Reception", "Desk staff", RoleScope.Tenant));
        Assert.Throws<DomainException>(() => Role.Create("SuperAdmin", "All", RoleScope.Platform, tenantId: Guid.NewGuid()));

        Role platformRole = Role.Create("BillingAdmin", "Billing", RoleScope.Platform, isSystem: true);
        Role tenantRole = Role.Create("Reception", "Desk staff", RoleScope.Tenant, tenantId: Guid.NewGuid());

        Assert.Null(platformRole.TenantId);
        Assert.NotNull(tenantRole.TenantId);
        Assert.IsType<RoleCreated>(Assert.Single(platformRole.DomainEvents));
    }

    [Fact]
    public void GrantAndRevokePermission_AreIdempotent_WithOneEventPerRealChange()
    {
        Role role = Role.Create("SupportAgent", "Support", RoleScope.Platform);
        var permissionId = Guid.NewGuid();
        role.DequeueDomainEvents();

        role.GrantPermission(permissionId);
        role.GrantPermission(permissionId); // no-op

        Assert.Single(role.Permissions);
        Assert.IsType<RolePermissionGranted>(Assert.Single(role.DequeueDomainEvents()));

        role.RevokePermission(permissionId);
        role.RevokePermission(permissionId); // no-op

        Assert.Empty(role.Permissions);
        Assert.IsType<RolePermissionRevoked>(Assert.Single(role.DequeueDomainEvents()));
    }

    [Fact]
    public void SystemRoles_CannotBeRenamed()
    {
        Role role = Role.Create("SuperAdmin", "All", RoleScope.Platform, isSystem: true);

        Assert.Throws<DomainException>(() => role.Rename("God", "Everything"));
    }

    [Fact]
    public void SystemRoleDefinitions_ReferenceOnlyDeclaredPermissions()
    {
        var declared = PlatformPermissions.All.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        foreach (SystemRoleDefinition definition in SystemRoles.Definitions)
        {
            Assert.All(definition.Permissions, key => Assert.Contains(key, declared));
        }
    }
}
