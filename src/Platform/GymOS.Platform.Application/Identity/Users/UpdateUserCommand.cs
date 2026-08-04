using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

/// <param name="ExpectedRowVersion">
/// The version the caller read, from <c>If-Match</c>. Required: this command overwrites fields the
/// caller previously read, so without it the write is last-write-wins.
/// </param>
[RequiresPermission(PlatformPermissions.UsersManage)]
public sealed record UpdateUserCommand(
    Guid UserId,
    string FirstName,
    string LastName,
    string? ExpectedRowVersion) : ICommand<Result<UserDetail>>;

internal sealed class UpdateUserCommandHandler : ICommandHandler<UpdateUserCommand, Result<UserDetail>>
{
    private readonly IUserRepository _users;
    private readonly IUserQueries _userQueries;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateUserCommandHandler(
        IUserRepository users,
        IUserQueries userQueries,
        IUnitOfWork unitOfWork)
    {
        _users = users;
        _userQueries = userQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserDetail>> HandleAsync(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        User? user = await _users.FindByIdAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return Result.Failure<UserDetail>(UserErrors.NotFound(command.UserId));
        }

        if (string.IsNullOrWhiteSpace(command.FirstName) || string.IsNullOrWhiteSpace(command.LastName))
        {
            return Result.Failure<UserDetail>(
                Error.Validation("users.invalid_input", "First and last name are required."));
        }

        Result version = UserWrites.ApplyExpectedVersion(
            _users, user, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<UserDetail>(version.Error!);
        }

        user.UpdateProfile(command.FirstName, command.LastName);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await UserWrites.ReadBackAsync(_userQueries, command.UserId, cancellationToken);
    }
}
