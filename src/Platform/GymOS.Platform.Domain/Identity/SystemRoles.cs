namespace GymOS.Platform.Domain.Identity;

/// <summary>A seeded platform role and its canonical grants.</summary>
public sealed record SystemRoleDefinition(
    string Name,
    string Description,
    bool GrantsAllPermissions,
    IReadOnlyList<string> Permissions);

/// <summary>
/// The seeded platform roles (ARCHITECTURE.md §2.1). Their grants are
/// code-canonical: the startup seeder reconciles the database to exactly this —
/// grants added here appear, grants removed here disappear.
/// </summary>
public static class SystemRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string PlatformAdmin = "PlatformAdmin";
    public const string SupportAgent = "SupportAgent";
    public const string BillingAdmin = "BillingAdmin";
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlyList<SystemRoleDefinition> Definitions =
    [
        new(SuperAdmin, "Full platform control", GrantsAllPermissions: true, []),
        new(
            PlatformAdmin,
            "Day-to-day platform administration",
            GrantsAllPermissions: false,
            [
                PlatformPermissions.GymsRead,
                PlatformPermissions.GymsCreate,
                PlatformPermissions.GymsUpdate,
                PlatformPermissions.GymsSuspend,
                PlatformPermissions.UsersRead,
                PlatformPermissions.UsersManage,
                PlatformPermissions.RolesRead,
                PlatformPermissions.AuditRead,

                // Read, deliberately not manage: what the platform charges is a commercial decision,
                // not day-to-day administration.
                PlatformPermissions.BillingRead,

                // Features are the other way round: turning a capability on for one customer IS
                // day-to-day administration, so this role holds manage as well as read.
                PlatformPermissions.FeaturesRead,
                PlatformPermissions.FeaturesManage,
            ]),
        new(
            SupportAgent,
            "Support: read users and audit trails",
            GrantsAllPermissions: false,
            [
                PlatformPermissions.GymsRead,
                PlatformPermissions.UsersRead,
                PlatformPermissions.AuditRead,

                // A support agent answering "what am I paying for?" needs the catalogue — and
                // "why is POS off for this gym?" needs the resolved features and their sources.
                PlatformPermissions.BillingRead,
                PlatformPermissions.FeaturesRead,
            ]),
        new(
            BillingAdmin,
            "Billing operations: the plan catalogue and its prices",
            GrantsAllPermissions: false,
            [
                PlatformPermissions.GymsRead,
                PlatformPermissions.UsersRead,
                PlatformPermissions.BillingRead,
                PlatformPermissions.BillingManage,
                PlatformPermissions.SubscriptionsManage,

                // What a plan includes is priced, so billing edits it — via `/plans/{id}/features`,
                // which is gated on billing.manage. Reading the catalogue is needed to do that.
                PlatformPermissions.FeaturesRead,
            ]),
        new(
            ReadOnly,
            "Read-only access across the platform",
            GrantsAllPermissions: false,
            [
                PlatformPermissions.GymsRead,
                PlatformPermissions.UsersRead,
                PlatformPermissions.RolesRead,
                PlatformPermissions.AuditRead,
                PlatformPermissions.BillingRead,
                PlatformPermissions.FeaturesRead,
            ]),
    ];
}
