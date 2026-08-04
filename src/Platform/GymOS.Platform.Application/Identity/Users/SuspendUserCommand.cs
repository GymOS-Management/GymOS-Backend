using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <summary>
/// Suspends an administrator and ends their sessions immediately.
///
/// Suspension that leaves the person signed in is not suspension. The domain rotates the
/// security stamp, which the bearer-token validator checks on every request; this handler
/// also revokes their refresh tokens, so neither the current access token nor a refresh
/// survives the call.
/// </summary>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record SuspendUserCommand(Guid UserId, string Reason) : ICommand<Result>;

internal sealed class SuspendUserCommandHandler : ICommandHandler<SuspendUserCommand, Result>
{
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly SuperAdminGuard _superAdminGuard;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public SuspendUserCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        SuperAdminGuard superAdminGuard,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _superAdminGuard = superAdminGuard;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<Result> HandleAsync(SuspendUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure(Error.Validation("users.reason_required", "A suspension reason is required."));
        }

        // Locking yourself out is never the intent, and an administrator who has done it
        // cannot undo it.
        if (_currentUser.UserId == command.UserId)
        {
            return Result.Failure(UserErrors.CannotSuspendSelf);
        }

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(command.UserId));
        }

        if (user.Status == UserStatus.Suspended)
        {
            return Result.Failure(UserErrors.AlreadySuspended);
        }

        Result guard = await _superAdminGuard.EnsureNotLastSuperAdminAsync(user, cancellationToken);
        if (guard.IsFailure)
        {
            return guard;
        }

        DateTimeOffset now = _clock.UtcNow;
        user.Suspend(command.Reason, now);

        string ipAddress = _currentUser.IpAddress ?? "unknown";
        IReadOnlyList<RefreshToken> tokens = await _refreshTokens.ListByUserAsync(user.Id, cancellationToken);
        foreach (RefreshToken token in tokens.Where(token => token.IsActive(now)))
        {
            token.Revoke(now, ipAddress, RevocationReasons.AccountSuspended);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record ReactivateUserCommand(Guid UserId) : ICommand<Result>;

internal sealed class ReactivateUserCommandHandler : ICommandHandler<ReactivateUserCommand, Result>
{
    private readonly IUserRepository _users;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ReactivateUserCommandHandler(IUserRepository users, IUnitOfWork unitOfWork, IClock clock)
    {
        _users = users;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result> HandleAsync(ReactivateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure(UserErrors.NotFound(command.UserId));
        }

        if (user.Status != UserStatus.Suspended)
        {
            return Result.Failure(UserErrors.NotSuspended);
        }

        // Reactivation restores PendingActivation for an account that never set a password —
        // it does not hand back a usable login the owner never had (domain behavior).
        user.Reactivate(_clock.UtcNow);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
