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
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOptions<LoginPolicyOptions> _policy;

    public ChangePasswordCommandHandler(
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<LoginPolicyOptions> policy)
    {
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _unitOfWork = unitOfWork;
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

        if (!_passwordHasher.Verify(command.CurrentPassword ?? string.Empty, user.PasswordHash))
        {
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

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
