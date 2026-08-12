using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.SignIn;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Application.Identity.Activation;

/// <summary>
/// Sets a password from an emailed link — the endpoint that makes an invited administrator usable.
///
/// <para><b>Anonymous by necessity</b>: the person holding the link has no way to sign in, which is
/// the situation it exists to resolve. The token is the authentication.</para>
///
/// <para><b>Exempt from the permission convention</b> for that reason, declared rather than assumed
/// (the convention test requires either an attribute or an explicit exemption).</para>
/// </summary>
[NoPermissionRequired("The caller cannot sign in yet — the one-time token in the link is the credential.")]
public sealed record SetPasswordCommand(string Token, string NewPassword) : ICommand<Result>;

/// <summary>
/// Whether a link is still usable, for the console to decide between a form and an apology.
/// </summary>
[NoPermissionRequired("Answers only whether an opaque token is live; reveals nothing about any account.")]
public sealed record CheckActivationTokenQuery(string Token) : IQuery<Result>;

internal sealed class SetPasswordCommandHandler : ICommandHandler<SetPasswordCommand, Result>
{
    /// <summary>
    /// Stands in for the caller's address on the revoked sessions. The person following the link is
    /// anonymous, and attributing the revocation to an arbitrary IP would read as though a known
    /// client did it.
    /// </summary>
    private const string RevokedByActivationLink = "activation-link";

    private readonly IUserActivationTokenRepository _tokens;
    private readonly IActivationTokenGenerator _generator;
    private readonly IUserRepository _users;
    private readonly IRefreshTokenRepository _refreshTokens;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOptions<LoginPolicyOptions> _policy;

    public SetPasswordCommandHandler(
        IUserActivationTokenRepository tokens,
        IActivationTokenGenerator generator,
        IUserRepository users,
        IRefreshTokenRepository refreshTokens,
        IPasswordHasher passwordHasher,
        IAuditTrail auditTrail,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOptions<LoginPolicyOptions> policy)
    {
        _tokens = tokens;
        _generator = generator;
        _users = users;
        _refreshTokens = refreshTokens;
        _passwordHasher = passwordHasher;
        _auditTrail = auditTrail;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _policy = policy;
    }

    public async Task<Result> HandleAsync(SetPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        DateTimeOffset now = _clock.UtcNow;

        UserActivationToken? token = string.IsNullOrWhiteSpace(command.Token)
            ? null
            : await _tokens.FindByHashAsync(_generator.ComputeHash(command.Token), cancellationToken);

        // Never existed, expired, already spent, or belongs to an account that has since gone: every
        // one answers identically. A distinct "expired" would confirm the token had once been real,
        // and the operator-facing nuance it buys is worth nothing beside that.
        if (token is null || !token.IsUsable(now))
        {
            return Result.Failure(ActivationErrors.LinkNotUsable);
        }

        User? user = await _users.FindByIdAsync(token.UserId, cancellationToken);
        if (user is null || user.Status == UserStatus.Suspended)
        {
            return Result.Failure(ActivationErrors.LinkNotUsable);
        }

        // Spent before the password is judged. Consuming only on success would leave the link
        // replayable for its whole lifetime to anyone willing to submit a password the policy
        // rejects — the attempt costs them nothing and the link survives it.
        if (!token.Consume(now))
        {
            return Result.Failure(ActivationErrors.LinkNotUsable);
        }

        Result policyCheck = PasswordPolicy.Validate(
            command.NewPassword, _policy.Value.MinimumPasswordLength);

        if (policyCheck.IsFailure)
        {
            // The token is spent either way — see above — so the consumption is committed before the
            // refusal is returned. The caller learns what was wrong with the password and needs a new
            // link, which is the honest cost of not leaving a replayable one behind.
            //
            // This commit touches the token and nothing else, which is why it needed the token's own
            // row version: with no user row in the transaction there was nothing else to arbitrate a
            // concurrent second consume.
            return await TryCommitAsync(cancellationToken)
                ? policyCheck
                : Result.Failure(ActivationErrors.LinkNotUsable);
        }

        // Rotates the security stamp, clears any lockout, and — for a PendingActivation account —
        // performs the activation itself through UserStatusTransitions. Already the domain's job
        // (AD-4); this handler does not re-implement any of it.
        user.SetPasswordHash(_passwordHasher.Hash(command.NewPassword), now);

        // A password change ends every session, and a reset has more reason to than most: the person
        // may be resetting *because* they believe somebody else is in. M1.2's rule for
        // change-password, applied where it matters more.
        IReadOnlyList<RefreshToken> sessions = await _refreshTokens.ListByUserAsync(
            user.Id, cancellationToken);

        foreach (RefreshToken session in sessions.Where(session => session.IsActive(now)))
        {
            session.Revoke(now, RevokedByActivationLink, RevocationReasons.PasswordChanged);
        }

        _auditTrail.Record(
            "user.password_set",
            entityType: "user",
            entityId: user.Id,
            details: new Dictionary<string, string?>
            {
                ["purpose"] = token.Purpose.ToString(),
            },
            subjectEmail: user.Email);

        return await TryCommitAsync(cancellationToken)
            ? Result.Success()
            : Result.Failure(ActivationErrors.LinkNotUsable);
    }

    /// <summary>
    /// Commits, translating a lost consume race into the ordinary "link not usable" refusal.
    ///
    /// <para>The token carries a row version, so a second request that read it while the first was
    /// still in flight loses here instead of spending the same link twice. That conflict is
    /// <em>not</em> a 409 for the caller to retry: the link is genuinely spent and a retry can only
    /// fail again. It collapses into the same answer every other spent link gets, which is also the
    /// only answer this endpoint documents.</para>
    /// </summary>
    private async Task<bool> TryCommitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);

            return true;
        }
        catch (ConcurrencyConflictException)
        {
            return false;
        }
    }
}

internal sealed class CheckActivationTokenQueryHandler : IQueryHandler<CheckActivationTokenQuery, Result>
{
    private readonly IUserActivationTokenRepository _tokens;
    private readonly IActivationTokenGenerator _generator;
    private readonly IClock _clock;

    public CheckActivationTokenQueryHandler(
        IUserActivationTokenRepository tokens,
        IActivationTokenGenerator generator,
        IClock clock)
    {
        _tokens = tokens;
        _generator = generator;
        _clock = clock;
    }

    public async Task<Result> HandleAsync(
        CheckActivationTokenQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        UserActivationToken? token = string.IsNullOrWhiteSpace(query.Token)
            ? null
            : await _tokens.FindByHashForReadingAsync(
                _generator.ComputeHash(query.Token), cancellationToken);

        // Validity and nothing else — no email, no name, no roles. The console needs to choose
        // between a form and an apology; anything more would make this a lookup service for anyone
        // holding a token.
        return token is not null && token.IsUsable(_clock.UtcNow)
            ? Result.Success()
            : Result.Failure(ActivationErrors.LinkNotUsable);
    }
}

/// <summary>The failure vocabulary of the activation surface. Codes are stable API.</summary>
public static class ActivationErrors
{
    /// <summary>
    /// One error for four causes, deliberately: never existed, expired, already used, or the account
    /// is gone or suspended. Splitting them would turn this endpoint into an oracle.
    /// </summary>
    public static Error LinkNotUsable { get; } = Error.NotFound(
        "auth.link_not_usable",
        "This link is no longer usable. Ask an administrator to send you a new one.");
}
