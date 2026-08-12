using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Activation;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <summary>
/// Creates a platform administrator in <c>PendingActivation</c> — no password.
///
/// An administrator never sets another administrator's password (ARCHITECTURE.md §3.6):
/// a password one other person has seen is not a credential. The account exists and holds
/// its roles but cannot authenticate until its owner sets a password through the
/// activation flow, which lands with email delivery.
/// </summary>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record InviteUserCommand(
    string Email,
    string FirstName,
    string LastName,
    IReadOnlyCollection<Guid> RoleIds) : ICommand<Result<InvitedUser>>;

/// <summary>
/// The created administrator, and whether the invitation reached the mail server.
///
/// <para>Two facts rather than one because they can differ: the account is created regardless, and an
/// unreachable SMTP server must not roll that back (ADR-53). The endpoint puts the user in the body,
/// exactly as before, and the delivery outcome in a response header — the shape
/// <c>Idempotency-Replayed</c> already set — so the console can say "created, but the email did not
/// go; re-send it" instead of implying an invitation is on its way.</para>
/// </summary>
public sealed record InvitedUser(UserDetail User, bool EmailSent);

internal sealed class InviteUserCommandHandler : ICommandHandler<InviteUserCommand, Result<InvitedUser>>
{
    private readonly IUserRepository _users;
    private readonly IRoleRepository _roles;
    private readonly IUserQueries _userQueries;
    private readonly ActivationIssuer _activation;
    private readonly IAuditTrail _auditTrail;
    private readonly IUnitOfWork _unitOfWork;

    public InviteUserCommandHandler(
        IUserRepository users,
        IRoleRepository roles,
        IUserQueries userQueries,
        ActivationIssuer activation,
        IAuditTrail auditTrail,
        IUnitOfWork unitOfWork)
    {
        _users = users;
        _roles = roles;
        _userQueries = userQueries;
        _activation = activation;
        _auditTrail = auditTrail;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<InvitedUser>> HandleAsync(InviteUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Email) || !command.Email.Contains('@', StringComparison.Ordinal))
        {
            return Result.Failure<InvitedUser>(UserErrors.InvalidEmail(command.Email ?? string.Empty));
        }

        string normalizedEmail = User.Normalize(command.Email);

        // Checked here as well as by the unique index: a typed conflict is a better answer
        // than a database exception, and the index remains the actual guarantee under a race.
        if (await _users.FindByNormalizedEmailAsync(normalizedEmail, cancellationToken) is not null)
        {
            return Result.Failure<InvitedUser>(UserErrors.EmailTaken(command.Email));
        }

        Result<IReadOnlyList<Role>> roles = await ResolveRolesAsync(command.RoleIds, cancellationToken);
        if (roles.IsFailure)
        {
            return Result.Failure<InvitedUser>(roles.Error!);
        }

        User user;
        try
        {
            user = User.Create(command.Email, command.FirstName, command.LastName);
        }
        catch (ArgumentException exception)
        {
            // Blank names reach the domain as an argument violation; report it as input
            // validation rather than letting it surface as a 500.
            return Result.Failure<InvitedUser>(Error.Validation("users.invalid_input", exception.Message));
        }
        catch (DomainException exception)
        {
            return Result.Failure<InvitedUser>(Error.Validation("users.invalid_input", exception.Message));
        }

        foreach (Role role in roles.Value)
        {
            user.AssignRole(role.Id);
        }

        _users.Add(user);

        // Staged inside the same transaction as the account, so an invitation link can never refer
        // to a user that was not created — and the account can never be created without one, which
        // is the state that made invited administrators unusable before ADR-53.
        string rawToken = await _activation.IssueAsync(
            user.Id, ActivationPurpose.Invitation, cancellationToken);

        _auditTrail.Record(
            "user.invitation_sent",
            entityType: "user",
            entityId: user.Id,
            // The token is deliberately absent. The trail records that an invitation went out and to
            // whom; recording the link would put a live credential in a table built to be read.
            details: new Dictionary<string, string?> { ["email"] = user.Email });

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // After the commit: a send that goes out before the save could deliver a link for a token
        // that never existed. See ActivationIssuer.SendAsync for what the reverse costs.
        EmailSendResult delivery = await _activation.SendAsync(
            ActivationPurpose.Invitation,
            new ActivationRecipient(user.Email, user.FirstName),
            rawToken,
            cancellationToken);

        // Read back rather than mapping the aggregate: the response then carries the same shape,
        // resolved permissions, and row version a subsequent GET would return, so a client never
        // has to reconcile two representations of the resource it just created.
        UserDetail? created = await _userQueries.GetAsync(user.Id, cancellationToken);

        return created is null
            ? Result.Failure<InvitedUser>(UserErrors.NotFound(user.Id))
            : Result.Success(new InvitedUser(created, delivery.Sent));
    }

    private async Task<Result<IReadOnlyList<Role>>> ResolveRolesAsync(
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        if (roleIds is null || roleIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<Role>>([]);
        }

        var requested = roleIds.Distinct().ToList();
        IReadOnlyList<Role> found = await _roles.ListByIdsAsync(requested, cancellationToken);

        var missing = requested.Where(id => found.All(role => role.Id != id)).ToList();
        if (missing.Count > 0)
        {
            return Result.Failure<IReadOnlyList<Role>>(UserErrors.UnknownRole(missing[0]));
        }

        // A gym-scoped role on a platform administrator would grant access inside a tenant
        // that nobody intended; refuse it rather than quietly ignoring it.
        Role? tenantScoped = found.FirstOrDefault(role => role.Scope != RoleScope.Platform);
        return tenantScoped is not null
            ? Result.Failure<IReadOnlyList<Role>>(UserErrors.TenantRoleNotAssignable(tenantScoped.Name))
            : Result.Success(found);
    }
}
