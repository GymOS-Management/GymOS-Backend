using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <summary>
/// Protects the platform from becoming unadministrable: the last active SuperAdmin cannot
/// be suspended or stripped of the role.
///
/// This rule lives in the Application layer, not in <see cref="User"/>, and that is not a
/// compromise — it is a constraint over the *set* of administrators, and an aggregate whose
/// job is to protect its own invariants has no business querying its siblings. Encoding it
/// in the domain would mean handing the aggregate a repository, which is how aggregates
/// stop being aggregates. It is centralized here so both callers apply the identical check.
/// </summary>
internal sealed class SuperAdminGuard
{
    private readonly IUserQueries _userQueries;
    private readonly IRoleRepository _roles;

    public SuperAdminGuard(IUserQueries userQueries, IRoleRepository roles)
    {
        _userQueries = userQueries;
        _roles = roles;
    }

    /// <summary>
    /// Fails when <paramref name="user"/> is an active SuperAdmin and no other active user
    /// holds the role.
    /// </summary>
    public async Task<Result> EnsureNotLastSuperAdminAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.Status != UserStatus.Active)
        {
            // An inactive user is not holding the platform up, so removing their role or
            // suspending them again cannot lock anyone out.
            return Result.Success();
        }

        Role? superAdmin = await _roles.FindPlatformRoleByNameAsync(SystemRoles.SuperAdmin, cancellationToken);
        if (superAdmin is null || user.Roles.All(assignment => assignment.RoleId != superAdmin.Id))
        {
            return Result.Success();
        }

        int activeSuperAdmins = await _userQueries.CountActiveUsersInRoleAsync(
            SystemRoles.SuperAdmin, cancellationToken);

        return activeSuperAdmins <= 1
            ? Result.Failure(UserErrors.LastSuperAdmin)
            : Result.Success();
    }

    /// <summary>
    /// Fails when the requested role set would remove SuperAdmin from the platform's last
    /// active holder of it.
    /// </summary>
    public async Task<Result> EnsureRoleChangeKeepsASuperAdminAsync(
        User user,
        IReadOnlyCollection<Guid> requestedRoleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(requestedRoleIds);

        Role? superAdmin = await _roles.FindPlatformRoleByNameAsync(SystemRoles.SuperAdmin, cancellationToken);
        if (superAdmin is null)
        {
            return Result.Success();
        }

        bool holdsIt = user.Roles.Any(assignment => assignment.RoleId == superAdmin.Id);
        bool keepsIt = requestedRoleIds.Contains(superAdmin.Id);

        return holdsIt && !keepsIt
            ? await EnsureNotLastSuperAdminAsync(user, cancellationToken)
            : Result.Success();
    }
}
