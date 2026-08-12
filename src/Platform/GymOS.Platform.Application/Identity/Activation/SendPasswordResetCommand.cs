using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Activation;

/// <summary>
/// ARCHITECTURE.md §3.6's <c>POST /users/{id}/reset-password</c>, finally built.
///
/// <para><b>It sends a link; it does not set a password.</b> That is the whole point of §3.6 — an
/// administrator who could set another administrator's password would hold a credential that is no
/// longer a credential. This hands the account's owner the means to choose one and tells the
/// initiator nothing about the result beyond "the message went".</para>
///
/// <para>Administrator-initiated only. A public "forgot password" needs its own anti-enumeration
/// treatment — identical answers whether or not the address exists — and is deliberately a separate
/// increment.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record SendPasswordResetCommand(Guid UserId) : ICommand<Result<ActivationDispatch>>;

internal sealed class SendPasswordResetCommandHandler
    : ICommandHandler<SendPasswordResetCommand, Result<ActivationDispatch>>
{
    private readonly IUserRepository _users;
    private readonly ActivationIssuer _activation;
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;

    public SendPasswordResetCommandHandler(
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
        SendPasswordResetCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<ActivationDispatch>(UserErrors.NotFound(command.UserId));
        }

        // A never-activated account gets an invitation, not a reset: the copy differs, the link lives
        // far longer, and telling someone to "reset" a password they never had is confusing at best.
        if (user.Status == UserStatus.PendingActivation)
        {
            return Result.Failure<ActivationDispatch>(UserErrors.StillPendingActivation(user.Email));
        }

        // A suspended account must not be handed a way back in. Reactivate it first, deliberately,
        // rather than letting a reset quietly undo the suspension.
        if (user.Status == UserStatus.Suspended)
        {
            return Result.Failure<ActivationDispatch>(UserErrors.SuspendedCannotReset(user.Email));
        }

        string rawToken = await _activation.IssueAsync(
            user.Id, ActivationPurpose.PasswordReset, cancellationToken);

        _auditTrail.Record(
            "user.password_reset_requested",
            entityType: "user",
            entityId: user.Id,
            details: new Dictionary<string, string?> { ["email"] = user.Email });

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        EmailSendResult delivery = await _activation.SendAsync(
            ActivationPurpose.PasswordReset,
            new ActivationRecipient(user.Email, user.FirstName),
            rawToken,
            cancellationToken);

        return Result.Success(new ActivationDispatch(delivery.Sent));
    }
}
