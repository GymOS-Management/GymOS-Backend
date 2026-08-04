using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <summary>
/// Replaces an administrator's role set wholesale (ARCHITECTURE.md §3.6).
///
/// Declarative rather than incremental — the caller sends the roles the user should end up
/// with, not a list of grants and revokes. Two clients editing the same user then converge
/// on a state instead of composing into one nobody chose, and the endpoint is naturally
/// idempotent.
/// </summary>
/// <param name="ExpectedRowVersion">
/// The version the caller read, from <c>If-Match</c>. Required: the request replaces a role set the
/// caller previously read, so without it two concurrent edits silently discard one another.
/// </param>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record SetUserRolesCommand(
    Guid UserId,
    IReadOnlyCollection<Guid> RoleIds,
    string? ExpectedRowVersion) : ICommand<Result<UserDetail>>;

internal sealed class SetUserRolesCommandHandler : ICommandHandler<SetUserRolesCommand, Result<UserDetail>>
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IUserQueries _userQueries;
    private readonly SuperAdminGuard _superAdminGuard;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public SetUserRolesCommandHandler(
        IUserRepository users,
        IRoleRepository roles,
        IUserQueries userQueries,
        SuperAdminGuard superAdminGuard,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser)
    {
        _users = users;
        _roles = roles;
        _userQueries = userQueries;
        _superAdminGuard = superAdminGuard;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<UserDetail>> HandleAsync(
        SetUserRolesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Self-elevation and self-demotion are both refused: an administrator editing their
        // own grants is either widening their own reach or removing the access someone else
        // will have to restore by hand.
        if (_currentUser.UserId == command.UserId)
        {
            return Result.Failure<UserDetail>(UserErrors.CannotChangeOwnRoles);
        }

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserDetail>(UserErrors.NotFound(command.UserId));
        }

        var requestedRoleIds = (command.RoleIds ?? []).Distinct().ToList();
        IReadOnlyList<Role> requestedRoles = await _roles.ListByIdsAsync(requestedRoleIds, cancellationToken);

        var missing = requestedRoleIds.Where(id => requestedRoles.All(role => role.Id != id)).ToList();
        if (missing.Count > 0)
        {
            return Result.Failure<UserDetail>(UserErrors.UnknownRole(missing[0]));
        }

        Role? tenantScoped = requestedRoles.FirstOrDefault(role => role.Scope != RoleScope.Platform);
        if (tenantScoped is not null)
        {
            return Result.Failure<UserDetail>(UserErrors.TenantRoleNotAssignable(tenantScoped.Name));
        }

        Result guard = await _superAdminGuard.EnsureRoleChangeKeepsASuperAdminAsync(
            user, requestedRoleIds, cancellationToken);
        if (guard.IsFailure)
        {
            return Result.Failure<UserDetail>(guard.Error!);
        }

        Result version = UserWrites.ApplyExpectedVersion(
            _users, user, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<UserDetail>(version.Error!);
        }

        // The delta is computed up front, against a snapshot of the current assignments:
        // iterating the live collection while mutating it is how a reconcile loop starts
        // depending on its own side effects. Unchanged assignments are not touched at all, so
        // they raise no event and leave no audit noise.
        var currentRoleIds = user.Roles
            .Where(assignment => assignment.TenantId is null)
            .Select(assignment => assignment.RoleId)
            .ToList();

        foreach (Guid roleId in currentRoleIds.Where(roleId => !requestedRoleIds.Contains(roleId)))
        {
            user.RevokeRole(roleId);
        }

        foreach (Guid roleId in requestedRoleIds.Where(roleId => !currentRoleIds.Contains(roleId)))
        {
            user.AssignRole(roleId);
        }

        // Role rows live in a child table, so without this the users row is never updated and its
        // version never moves — leaving concurrent role edits unprotected. Only when something
        // actually changed: a no-op PUT should not invent a new version for clients to chase.
        if (!currentRoleIds.ToHashSet().SetEquals(requestedRoleIds))
        {
            _users.MarkAggregateChanged(user);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await UserWrites.ReadBackAsync(_userQueries, command.UserId, cancellationToken);
    }
}
