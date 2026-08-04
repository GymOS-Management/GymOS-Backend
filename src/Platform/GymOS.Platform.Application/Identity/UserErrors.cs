using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity;

/// <summary>Failure vocabulary of the platform-users surface. Codes are stable API.</summary>
public static class UserErrors
{
    public static Error NotFound(Guid userId) => Error.NotFound(
        "users.not_found",
        $"No platform administrator with id '{userId}' exists.");

    public static Error EmailTaken(string email) => Error.Conflict(
        "users.email_taken",
        $"A platform administrator with email '{email}' already exists.");

    public static Error InvalidEmail(string email) => Error.Validation(
        "users.invalid_email",
        $"'{email}' is not a valid email address.");

    public static Error UnknownRole(Guid roleId) => Error.Validation(
        "users.unknown_role",
        $"No platform role with id '{roleId}' exists.");

    public static Error TenantRoleNotAssignable(string roleName) => Error.Validation(
        "users.role_not_platform_scoped",
        $"Role '{roleName}' is gym-scoped and cannot be granted to a platform administrator.");

    /// <summary>
    /// The rule that keeps the platform administrable: the last active SuperAdmin cannot be
    /// demoted or suspended. Without it, one ordinary-looking edit can leave an
    /// installation with nobody able to grant anyone else access — recoverable only by
    /// hand-editing the database.
    /// </summary>
    public static Error LastSuperAdmin => Error.Conflict(
        "users.last_super_admin",
        "This is the only active SuperAdmin. Grant the role to another administrator first.");

    public static Error CannotSuspendSelf => Error.Conflict(
        "users.cannot_suspend_self",
        "You cannot suspend your own account.");

    public static Error CannotChangeOwnRoles => Error.Conflict(
        "users.cannot_change_own_roles",
        "You cannot change your own roles. Ask another administrator.");

    public static Error NotSuspended => Error.Conflict(
        "users.not_suspended",
        "Only a suspended administrator can be reactivated.");

    public static Error AlreadySuspended => Error.Conflict(
        "users.already_suspended",
        "The administrator is already suspended.");
}
