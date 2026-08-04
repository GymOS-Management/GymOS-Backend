using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Application.Identity.SignIn;

/// <summary>
/// The sign-in use case. Check order is a security decision, not a convenience:
/// lockout is evaluated before the password is verified (a locked account must not
/// buy an attacker Argon2 work), and suspension is evaluated only after the password
/// verifies (otherwise the endpoint reports which accounts exist).
/// </summary>
internal sealed partial class SignInCommandHandler
    : ICommandHandler<SignInCommand, Result<AuthenticatedSession>>
{
    /// <summary>Recorded in the audit trail's <c>reason</c> detail — a stable value, not prose.</summary>
    internal const string ActionSignedIn = "auth.signed_in";
    internal const string ActionSignInFailed = "auth.sign_in_failed";

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _passwordHasher;
    private readonly SessionFactory _sessionFactory;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditTrail _auditTrail;
    private readonly IClock _clock;
    private readonly IOptions<LoginPolicyOptions> _policy;
    private readonly ILogger<SignInCommandHandler> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in succeeded for user {UserId}.")]
    private static partial void LogSignInSucceeded(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Sign-in failed ({Reason}) for normalized email {NormalizedEmail} from {IpAddress}.")]
    private static partial void LogSignInFailed(
        ILogger logger, string reason, string normalizedEmail, string ipAddress);

    public SignInCommandHandler(
        IUserRepository users,
        IPasswordHasher passwordHasher,
        SessionFactory sessionFactory,
        IUnitOfWork unitOfWork,
        IAuditTrail auditTrail,
        IClock clock,
        IOptions<LoginPolicyOptions> policy,
        ILogger<SignInCommandHandler> logger)
    {
        _users = users;
        _passwordHasher = passwordHasher;
        _sessionFactory = sessionFactory;
        _unitOfWork = unitOfWork;
        _auditTrail = auditTrail;
        _clock = clock;
        _policy = policy;
        _logger = logger;
    }

    public async Task<Result<AuthenticatedSession>> HandleAsync(
        SignInCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Read the clock once at the start of the use case and pass the instant
        // inward, so every decision below judges the same "now" (AD-40).
        DateTimeOffset now = _clock.UtcNow;
        string normalizedEmail = string.IsNullOrWhiteSpace(command.Email)
            ? string.Empty
            : User.Normalize(command.Email);

        User? user = normalizedEmail.Length == 0
            ? null
            : await _users.FindByNormalizedEmailAsync(normalizedEmail, cancellationToken);

        if (user?.PasswordHash is null)
        {
            // Unknown email and un-activated invitation must cost the same as a wrong
            // password, or response time alone answers "does this account exist?".
            _ = _passwordHasher.Hash(command.Password ?? string.Empty);
            return await FailAsync(
                "unknown_or_inactive", normalizedEmail, command, user?.Id,
                AuthenticationErrors.InvalidCredentials, cancellationToken);
        }

        if (user.IsLockedOut(now))
        {
            return await FailAsync(
                "locked_out", normalizedEmail, command, user.Id,
                AuthenticationErrors.AccountLocked, cancellationToken);
        }

        if (!_passwordHasher.Verify(command.Password ?? string.Empty, user.PasswordHash))
        {
            user.RecordFailedLogin(now, _policy.Value.MaxFailedAttempts, _policy.Value.LockoutDuration);
            return await FailAsync(
                "wrong_password", normalizedEmail, command, user.Id,
                AuthenticationErrors.InvalidCredentials, cancellationToken);
        }

        // Only now that the caller has proven they own the account is it safe to say
        // more than "invalid credentials".
        if (user.Status == UserStatus.Suspended)
        {
            return await FailAsync(
                "suspended", normalizedEmail, command, user.Id,
                AuthenticationErrors.AccountSuspended, cancellationToken);
        }

        user.RecordSuccessfulLogin(now);
        AuthenticatedSession session = await _sessionFactory.IssueAsync(
            user, command.IpAddress, now, cancellationToken);

        _auditTrail.Record(ActionSignedIn, entityType: "user", entityId: user.Id, subjectEmail: user.Email);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        LogSignInSucceeded(_logger, user.Id);
        return Result.Success(session);
    }

    /// <summary>
    /// Every rejected sign-in is recorded, with the reason the caller is never told.
    /// The response stays deliberately vague (see <see cref="AuthenticationErrors"/>); the
    /// audit trail is where an administrator can actually distinguish a forgotten password
    /// from someone probing a suspended account.
    /// </summary>
    private async Task<Result<AuthenticatedSession>> FailAsync(
        string reason,
        string normalizedEmail,
        SignInCommand command,
        Guid? userId,
        Error error,
        CancellationToken cancellationToken)
    {
        LogSignInFailed(_logger, reason, normalizedEmail, command.IpAddress);

        _auditTrail.Record(
            ActionSignInFailed,
            entityType: userId is null ? null : "user",
            entityId: userId,
            details: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["attemptedEmail"] = normalizedEmail,
            },
            subjectEmail: command.Email);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Failure<AuthenticatedSession>(error);
    }
}
