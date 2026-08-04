using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.SignOut;

/// <summary>
/// Sign-out is idempotent and always succeeds. A missing, unknown, foreign, or
/// already-revoked token still returns success: the caller's goal — "this session is
/// over" — is satisfied either way, and reporting the difference would let an
/// authenticated user probe whether a given token string is live.
/// </summary>
internal sealed class SignOutCommandHandler : ICommandHandler<SignOutCommand, Result>
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SignOutCommandHandler(
        IRefreshTokenRepository refreshTokens,
        IRefreshTokenGenerator refreshTokenGenerator,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _refreshTokens = refreshTokens;
        _refreshTokenGenerator = refreshTokenGenerator;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result> HandleAsync(SignOutCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Result.Success();
        }

        DateTimeOffset now = _clock.UtcNow;
        string tokenHash = _refreshTokenGenerator.ComputeHash(command.RefreshToken);
        RefreshToken? token = await _refreshTokens.FindByHashAsync(tokenHash, cancellationToken);

        if (token is null || token.UserId != command.UserId || token.IsRevoked)
        {
            return Result.Success();
        }

        token.Revoke(now, command.IpAddress, RevocationReasons.SignedOut);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
