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

    /// <summary>
    /// Assigning a plan to a gym, changing it, and cancelling (§3.3). Separate from
    /// <see cref="BillingManage"/> deliberately: deciding what the platform sells and deciding what one
    /// customer pays are different authorities, and the second is the one that costs money when it is
    /// wrong.
    /// </summary>
    public const string SubscriptionsManage = "subscriptions.manage";

    /// <summary>
    /// The feature catalogue and per-gym overrides (§3.4/§3.5). Separate from <see cref="BillingManage"/>
    /// because they answer different questions: what a plan *includes* is a commercial decision made
    /// once for every customer on it, while an override is an exception granted to one gym — which is
    /// day-to-day administration, and the reason support staff need it without holding the price list.
    /// </summary>
    public const string FeaturesRead = "features.read";
    public const string FeaturesManage = "features.manage";

    /// <summary>
    /// The dashboard's numbers and the analytics series (§3.2, §3.8).
    ///
    /// <para>Deliberately <b>not</b> granted to SupportAgent. MRR and churn are commercially
    /// sensitive, and the support question this platform exists to answer — "why can this gym not do
    /// X?" — needs the feature resolution, not the revenue. Same authority reasoning that keeps
    /// <c>features.manage</c> apart from <c>billing.manage</c>.</para>
    /// </summary>
    public const string AnalyticsRead = "analytics.read";

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
        new(SubscriptionsManage, ModuleName, "Assign, change, and cancel a gym's subscription"),
        new(FeaturesRead, ModuleName, "View the feature catalogue and a gym's resolved features"),
        new(FeaturesManage, ModuleName, "Edit the feature catalogue and set or clear gym overrides"),
        new(AnalyticsRead, ModuleName, "View the dashboard summary and analytics series"),
    ];
}
