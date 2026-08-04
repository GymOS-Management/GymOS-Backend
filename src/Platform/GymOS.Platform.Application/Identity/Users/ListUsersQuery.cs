using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Users;

[RequiresPermission(PlatformPermissions.UsersRead)]
public sealed record ListUsersQuery(PageRequest Page, string? Status)
    : IQuery<Result<PagedResult<UserListItem>>>;

internal sealed class ListUsersQueryHandler
    : IQueryHandler<ListUsersQuery, Result<PagedResult<UserListItem>>>
{
    private readonly IUserQueries _userQueries;

    public ListUsersQueryHandler(IUserQueries userQueries) => _userQueries = userQueries;

    public async Task<Result<PagedResult<UserListItem>>> HandleAsync(
        ListUsersQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        PagedResult<UserListItem> page = await _userQueries.ListAsync(
            new UserListFilter(query.Page, query.Status),
            cancellationToken);

        return Result.Success(page);
    }
}

[RequiresPermission(PlatformPermissions.UsersRead)]
public sealed record GetUserQuery(Guid UserId) : IQuery<Result<UserDetail>>;

internal sealed class GetUserQueryHandler : IQueryHandler<GetUserQuery, Result<UserDetail>>
{
    private readonly IUserQueries _userQueries;

    public GetUserQueryHandler(IUserQueries userQueries) => _userQueries = userQueries;

    public async Task<Result<UserDetail>> HandleAsync(GetUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        UserDetail? user = await _userQueries.GetAsync(query.UserId, cancellationToken);

        return user is null
            ? Result.Failure<UserDetail>(UserErrors.NotFound(query.UserId))
            : Result.Success(user);
    }
}
