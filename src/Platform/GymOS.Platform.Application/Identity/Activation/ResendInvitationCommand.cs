using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Activation;

/// <summary>
/// Sends a fresh invitation link to an administrator who has not activated yet.
///
/// <para><b>The recovery path for a lost email</b> — and the reason ADR-53 can accept an inline send
/// at all. The outbox processor does not exist, so a crash between the commit and the send drops one
/// message; without this, that account would be unusable forever.</para>
///
/// <para>Issuing invalidates the previous link, so at most one is ever live.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record ResendInvitationCommand(Guid UserId) : ICommand<Result<ActivationDispatch>>;

/// <summary>Whether the message reached the mail server. Never says "delivered" — nothing here can.</summary>
public sealed record ActivationDispatch(bool EmailSent);

internal sealed class ResendInvitationCommandHandler
    : ICommandHandler<ResendInvitationCommand, Result<ActivationDispatch>>
{
    private readonly IUserRepository _users;
    private readonly ActivationIssuer _activation;
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;

    public ResendInvitationCommandHandler(
        IUserRepository users,
        ActivationIssuer activation,
        IAuditTrail auditTrail,
        IUnitOfWork unitOfWork)
    {
        _users = users;
        _activation = activation;
        _auditTrail = auditTrail;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<ActivationDispatch>> HandleAsync(
        ResendInvitationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<ActivationDispatch>(UserErrors.NotFound(command.UserId));
        }

        // Only an account that has never been activated. On an Active one this is a password reset —
        // a different message with a much shorter link — and quietly sending the wrong one would tell
        // the recipient their account was never set up.
        if (user.Status != UserStatus.PendingActivation)
        {
            return Result.Failure<ActivationDispatch>(UserErrors.NotPendingActivation(user.Email));
        }

        string rawToken = await _activation.IssueAsync(
            user.Id, ActivationPurpose.Invitation, cancellationToken);

        _auditTrail.Record(
            "user.invitation_sent",
            entityType: "user",
            entityId: user.Id,
            details: new Dictionary<string, string?> { ["email"] = user.Email, ["resent"] = "true" });

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        EmailSendResult delivery = await _activation.SendAsync(
            ActivationPurpose.Invitation,
            new ActivationRecipient(user.Email, user.FirstName),
            rawToken,
            cancellationToken);

        return Result.Success(new ActivationDispatch(delivery.Sent));
    }
}
