using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Application.Identity.RefreshSession;

/// <summary>
/// Rotation with reuse detection (AD-4). The rule that gives the mechanism its teeth:
/// a token that has already been rotated must never be accepted again. Presenting one
/// means the token leaked — either the attacker is replaying a stolen copy, or the
/// legitimate client is replaying after the attacker already used it. Both cases are
/// answered the same way: revoke everything downstream of the presented token, so the
/// session the thief holds dies along with it.
/// </summary>
internal sealed partial class RefreshSessionCommandHandler
    : ICommandHandler<RefreshSessionCommand, Result<AuthenticatedSession>>
{
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IUserRepository _users;
    private readonly IRefreshTokenGenerator _refreshTokenGenerator;
    private readonly SessionFactory _sessionFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly ILogger<RefreshSessionCommandHandler> _logger;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Refresh-token reuse detected for user {UserId} from {IpAddress}: " +
        "revoked {RevokedCount} descendant token(s) in the rotation chain.")]
    private static partial void LogReuseDetected(
        ILogger logger, Guid userId, string ipAddress, int revokedCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh rejected: {Reason}.")]
    private static partial void LogRefreshRejected(ILogger logger, string reason);

    public RefreshSessionCommandHandler(
        IRefreshTokenRepository refreshTokens,
        IUserRepository users,
        IRefreshTokenGenerator refreshTokenGenerator,
        SessionFactory sessionFactory,
        IUnitOfWork unitOfWork,
        IClock clock,
        ILogger<RefreshSessionCommandHandler> logger)
    {
        _refreshTokens = refreshTokens;
        _users = users;
        _refreshTokenGenerator = refreshTokenGenerator;
        _sessionFactory = sessionFactory;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task<Result<AuthenticatedSession>> HandleAsync(
        RefreshSessionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset now = _clock.UtcNow;

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            LogRefreshRejected(_logger, "no token presented");
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.InvalidRefreshToken);
        }

        string presentedHash = _refreshTokenGenerator.ComputeHash(command.RefreshToken);

        // Rotation is a read-then-write, so the whole of it runs in one transaction over a locked
        // read. Without that, two simultaneous uses of one token both see it unrevoked and both
        // succeed — a replayed stolen token would quietly yield a second live session and reuse
        // detection would never fire, which is the one guarantee this handler exists to provide.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);

        RefreshToken? presented = await _refreshTokens.FindByHashForUpdateAsync(
            presentedHash, cancellationToken);

        if (presented is null)
        {
            LogRefreshRejected(_logger, "unknown token");
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.InvalidRefreshToken);
        }

        if (presented.IsRevoked)
        {
            await RevokeChainAsync(presented, command.IpAddress, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.InvalidRefreshToken);
        }

        if (!presented.IsActive(now))
        {
            LogRefreshRejected(_logger, "expired token");
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.InvalidRefreshToken);
        }

        User? user = await _users.FindByIdAsync(presented.UserId, cancellationToken);
        if (user?.PasswordHash is null)
        {
            LogRefreshRejected(_logger, "owner missing or deactivated");
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.InvalidRefreshToken);
        }

        // The refresh boundary is where suspension actually bites: it is the only
        // moment the platform re-reads the account state, so a suspended admin's
        // sessions cannot outlive one access-token lifetime (AD-4).
        if (user.Status == UserStatus.Suspended)
        {
            presented.Revoke(now, command.IpAddress, RevocationReasons.AccountSuspended);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result.Failure<AuthenticatedSession>(AuthenticationErrors.AccountSuspended);
        }

        AuthenticatedSession session = await _sessionFactory.IssueAsync(
            user, command.IpAddress, now, cancellationToken, replacing: presented);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(session);
    }

    /// <summary>
    /// Walks the rotation chain forward from the replayed token and revokes every
    /// still-active successor. Descendants only, deliberately: the user's other
    /// devices hold chains of their own, and a leak on one device is no reason to
    /// sign them out everywhere.
    /// </summary>
    private async Task RevokeChainAsync(
        RefreshToken replayed,
        string ipAddress,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RefreshToken> userTokens =
            await _refreshTokens.ListByUserAsync(replayed.UserId, cancellationToken);

        Dictionary<string, RefreshToken> byHash = userTokens
            .GroupBy(token => token.TokenHash, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var visited = new HashSet<string>(StringComparer.Ordinal) { replayed.TokenHash };
        int revokedCount = 0;
        string? nextHash = replayed.ReplacedByTokenHash;

        while (nextHash is not null && visited.Add(nextHash) && byHash.TryGetValue(nextHash, out RefreshToken? successor))
        {
            nextHash = successor.ReplacedByTokenHash;

            if (!successor.IsRevoked)
            {
                successor.Revoke(now, ipAddress, RevocationReasons.ReuseDetected);
                revokedCount++;
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        LogReuseDetected(_logger, replayed.UserId, ipAddress, revokedCount);
    }
}
