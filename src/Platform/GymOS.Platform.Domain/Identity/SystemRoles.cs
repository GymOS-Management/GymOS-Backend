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
            ]),
        new(
            SupportAgent,
            "Support: read users and audit trails",
            GrantsAllPermissions: false,
            [PlatformPermissions.GymsRead, PlatformPermissions.UsersRead, PlatformPermissions.AuditRead]),
        new(
            BillingAdmin,
            "Billing operations (billing permissions arrive with M3)",
            GrantsAllPermissions: false,
            [PlatformPermissions.GymsRead, PlatformPermissions.UsersRead]),
        new(
            ReadOnly,
            "Read-only access across the platform",
            GrantsAllPermissions: false,
            [
                PlatformPermissions.GymsRead,
                PlatformPermissions.UsersRead,
                PlatformPermissions.RolesRead,
                PlatformPermissions.AuditRead,
            ]),
    ];
}
