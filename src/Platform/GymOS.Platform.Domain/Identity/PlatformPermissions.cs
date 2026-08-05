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

    public const string GymsRead = "gyms.read";
    public const string GymsCreate = "gyms.create";
    public const string GymsUpdate = "gyms.update";
    public const string GymsSuspend = "gyms.suspend";
    public const string GymsDelete = "gyms.delete";

    public const string UsersRead = "users.read";
    public const string UsersManage = "users.manage";
    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";
    public const string AuditRead = "audit.read";

    /// <summary>
    /// The billing catalogue: plans, their prices, and (from M3.3) invoices. One permission pair for the
    /// commercial surface, matching §3.4's `billing.manage` on invoices — assigning a plan to a gym is
    /// the separate `subscriptions.manage`, because deciding what to sell and deciding what one customer
    /// pays are different authorities.
    /// </summary>
    public const string BillingRead = "billing.read";
    public const string BillingManage = "billing.manage";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(GymsRead, ModuleName, "View gyms and their details"),
        new(GymsCreate, ModuleName, "Provision a new gym"),
        new(GymsUpdate, ModuleName, "Edit a gym's details and branding"),
        new(GymsSuspend, ModuleName, "Suspend and reactivate a gym"),
        new(GymsDelete, ModuleName, "Delete a gym"),
        new(UsersRead, ModuleName, "View platform administrators"),
        new(UsersManage, ModuleName, "Create, edit, suspend, and reset platform administrators"),
        new(RolesRead, ModuleName, "View roles and their permission grants"),
        new(RolesManage, ModuleName, "Create roles and change permission grants"),
        new(AuditRead, ModuleName, "Read the audit log"),
        new(BillingRead, ModuleName, "View subscription plans and prices"),
        new(BillingManage, ModuleName, "Create and edit subscription plans and prices"),
    ];
}
