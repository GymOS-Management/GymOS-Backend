using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.Roles;

[RequiresPermission(PlatformPermissions.RolesRead)]
public sealed record ListRolesQuery : IQuery<Result<IReadOnlyList<RoleDetail>>>;

internal sealed class ListRolesQueryHandler : IQueryHandler<ListRolesQuery, Result<IReadOnlyList<RoleDetail>>>
{
    private readonly IRoleQueries _roleQueries;

    public ListRolesQueryHandler(IRoleQueries roleQueries) => _roleQueries = roleQueries;

    public async Task<Result<IReadOnlyList<RoleDetail>>> HandleAsync(
        ListRolesQuery query,
        CancellationToken cancellationToken) =>
        Result.Success(await _roleQueries.ListPlatformRolesAsync(cancellationToken));
}

[RequiresPermission(PlatformPermissions.RolesRead)]
public sealed record GetRoleQuery(Guid RoleId) : IQuery<Result<RoleDetail>>;

internal sealed class GetRoleQueryHandler : IQueryHandler<GetRoleQuery, Result<RoleDetail>>
{
    private readonly IRoleQueries _roleQueries;

    public GetRoleQueryHandler(IRoleQueries roleQueries) => _roleQueries = roleQueries;

    public async Task<Result<RoleDetail>> HandleAsync(GetRoleQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        RoleDetail? role = await _roleQueries.GetAsync(query.RoleId, cancellationToken);

        return role is null
            ? Result.Failure<RoleDetail>(Error.NotFound("roles.not_found", $"No role with id '{query.RoleId}' exists."))
            : Result.Success(role);
    }
}
