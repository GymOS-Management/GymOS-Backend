using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Application.Identity.ChangePassword;

/// <summary>
/// Changing a password ends every session, including the caller's. The domain rotates
/// the security stamp, which expires outstanding access tokens within one lifetime;
/// this handler revokes the refresh tokens, which is what stops a thief from simply
/// refreshing back in. Half the job would be worse than none — the client is expected
/// to sign in again.
/// </summary>
internal sealed class ChangePasswordCommandHandler : ICommandHandler<ChangePasswordCommand, Result>
{
    /// <summary>Audit actions — stable values, like <c>SignInCommandHandler</c>'s.</summary>
    internal const string ActionPasswordChanged = "auth.password_changed";
    internal const string ActionPasswordChangeFailed = "auth.password_change_failed";

    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditTrail _auditTrail;
    private readonly IClock _clock;
    private readonly IOptions<LoginPolicyOptions> _policy;

    public ChangePasswordCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        IUnitOfWork unitOfWork,
        IAuditTrail auditTrail,
        IClock clock,
        IOptions<LoginPolicyOptions> policy)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _clock = clock;
        _policy = policy;
    }

    public async Task<Result> HandleAsync(ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset now = _clock.UtcNow;

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user?.PasswordHash is null)
        {
            return Result.Failure(AuthenticationErrors.UserNotFound);
        }

        // Lockout is checked BEFORE the current password is verified, exactly as on sign-in: a
        // locked account must not buy an attacker Argon2 work, and a stolen access token must not
        // reach an unthrottled password oracle. Same counter as sign-in on purpose — guesses here
        // and at the login endpoint are attempts on one account, whichever door they knock on.
        if (user.IsLockedOut(now))
        {
            await FailAsync(user, "locked_out", cancellationToken);
            return Result.Failure(AuthenticationErrors.AccountLocked);
        }

        if (!_passwordHasher.Verify(command.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
            // Counted and audited: without this the endpoint was an unlimited, traceless oracle,
            // and a successful guess ends every session — locking the real owner out silently.
            await _users.RecordFailedLoginAsync(
                user, now, _policy.Value.MaxFailedAttempts, _policy.Value.LockoutDuration, cancellationToken);
            await FailAsync(user, "wrong_current_password", cancellationToken);
            return Result.Failure(AuthenticationErrors.InvalidCurrentPassword);
        }

        Result policyCheck = PasswordPolicy.Validate(command.NewPassword, _policy.Value.MinimumPasswordLength);
        if (policyCheck.IsFailure)
        {
            return policyCheck;
        }

        // Rotates the security stamp and clears any lockout (domain behavior, AD-4).
        user.SetPasswordHash(_passwordHasher.Hash(command.NewPassword), now);

        IReadOnlyList<RefreshToken> tokens = await _refreshTokens.ListByUserAsync(user.Id, cancellationToken);
        foreach (RefreshToken token in tokens.Where(token => token.IsActive(now)))
        {
            token.Revoke(now, command.IpAddress, RevocationReasons.PasswordChanged);
        }

        _auditTrail.Record(
            ActionPasswordChanged, entityType: "user", entityId: user.Id, subjectEmail: user.Email);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>
    /// Records a rejected attempt — and commits the failed-attempt counter with it, so lockout
    /// actually advances. The caller still learns only that the current password was wrong.
    /// </summary>
    private async Task FailAsync(User user, string reason, CancellationToken cancellationToken)
    {
        _auditTrail.Record(
            ActionPasswordChangeFailed,
            entityType: "user",
            entityId: user.Id,
            details: new Dictionary<string, string?>(StringComparer.Ordinal) { ["reason"] = reason },
            subjectEmail: user.Email);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
