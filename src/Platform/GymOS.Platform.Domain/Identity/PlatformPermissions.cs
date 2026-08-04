namespace GymOS.Platform.Domain.Identity;

/// <summary>One catalog entry a module declares (AD-22).</summary>
public sealed record PermissionDefinition(string Key, string Module, string Description);

/// <summary>
/// The platform module's declared permission catalog — the single source the
/// startup seeder syncs into the database. Grows with the milestones; keys are
/// stable API and never renamed. (Moves onto the IModule manifest when the module
/// loader lands.)
/// </summary>
public static class PlatformPermissions
{
    public const string ModuleName = "platform";

    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(UsersRead, ModuleName, "View platform administrators"),
        new(UsersManage, ModuleName, "Create, edit, suspend, and reset platform administrators"),
        new(RolesRead, ModuleName, "View roles and their permission grants"),
        new(RolesManage, ModuleName, "Create roles and change permission grants"),
        new(AuditRead, ModuleName, "Read the audit log"),
    ];
}
