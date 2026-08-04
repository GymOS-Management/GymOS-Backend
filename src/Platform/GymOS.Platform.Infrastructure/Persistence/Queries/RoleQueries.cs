using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class RoleQueries : IRoleQueries
{
    private readonly AppDbContext _dbContext;

    public RoleQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<RoleDetail>> ListPlatformRolesAsync(CancellationToken cancellationToken) =>
        await ProjectAsync(
            _dbContext.Set<Role>().Where(role => role.Scope == RoleScope.Platform).OrderBy(role => role.Name),
            cancellationToken);

    public async Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleDetail> roles = await ProjectAsync(
            _dbContext.Set<Role>().Where(role => role.Id == roleId),
            cancellationToken);

        return roles.Count == 0 ? null : roles[0];
    }

    /// <summary>
    /// Projects into an anonymous type, then shapes the DTO in memory.
    ///
    /// Two things force this rather than a single <c>Select</c> into <see cref="RoleDetail"/>:
    /// <c>Scope.ToString()</c> has no SQL translation, and a correlated collection projected
    /// straight into a positional record constructor does not translate either. Both fail at
    /// runtime rather than at compile time, so the shape here is deliberate — not incidental.
    /// The role count is small and bounded (seeded platform roles), so the in-memory step
    /// costs nothing worth optimizing.
    /// </summary>
    private async Task<IReadOnlyList<RoleDetail>> ProjectAsync(
        IQueryable<Role> roles,
        CancellationToken cancellationToken)
    {
        var projections = await roles
            .AsNoTracking()
            .Select(role => new
            {
                role.Id,
                role.Name,
                role.Description,
                role.Scope,
                role.IsSystem,
                AssignedUserCount = _dbContext.Set<UserRole>().Count(assignment => assignment.RoleId == role.Id),
                Permissions = _dbContext.Set<RolePermission>()
                    .Where(grant => grant.RoleId == role.Id)
                    .Join(
                        _dbContext.Set<Permission>(),
                        grant => grant.PermissionId,
                        permission => permission.Id,
                        (_, permission) => permission.Key)
                    .OrderBy(key => key)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        return projections
            .Select(role => new RoleDetail(
                role.Id,
                role.Name,
                role.Description,
                role.Scope.ToString(),
                role.IsSystem,
                role.AssignedUserCount,
                role.Permissions))
            .ToList();
    }
}
