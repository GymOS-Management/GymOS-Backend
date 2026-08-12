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

    /// <summary>
    /// A PUT arrived with no <c>If-Match</c>. Refused rather than defaulted, because defaulting to
    /// "overwrite whatever is there" is last-write-wins with extra steps. 428 at the HTTP edge —
    /// the status that exists to say "send the precondition and try again".
    /// </summary>
    public static Error VersionRequired => Error.Conflict(
        "concurrency.version_required",
        "This update requires an If-Match header carrying the rowVersion you read. " +
        "Re-read the resource and send its version.");

    public static Error MalformedVersion => Error.Validation(
        "concurrency.malformed_version",
        "The If-Match header is not a version token issued by this API. " +
        "Send the rowVersion from the resource, quoted, without a weak (W/) prefix.");

    public static Error NotSuspended => Error.Conflict(
        "users.not_suspended",
        "Only a suspended administrator can be reactivated.");

    public static Error AlreadySuspended => Error.Conflict(
        "users.already_suspended",
        "The administrator is already suspended.");

    /// <summary>
    /// Re-sending an invitation to somebody who has already activated. Refused rather than silently
    /// converted to a reset: the two messages read differently, and telling an established
    /// administrator their account was never set up is worse than an error.
    /// </summary>
    public static Error NotPendingActivation(string email) => Error.Conflict(
        "users.not_pending_activation",
        $"{email} has already activated their account. Send a password reset instead.");

    /// <summary>The mirror: resetting the password of somebody who never had one.</summary>
    public static Error StillPendingActivation(string email) => Error.Conflict(
        "users.still_pending_activation",
        $"{email} has never activated their account, so there is no password to reset. " +
        "Re-send the invitation instead.");

    /// <summary>
    /// A suspended administrator must not be handed a way back in. Reactivate first, deliberately —
    /// a reset that quietly undid a suspension would be a suspension that did not hold.
    /// </summary>
    public static Error SuspendedCannotReset(string email) => Error.Conflict(
        "users.suspended_cannot_reset",
        $"{email} is suspended. Reactivate the account before sending a password reset.");
}
