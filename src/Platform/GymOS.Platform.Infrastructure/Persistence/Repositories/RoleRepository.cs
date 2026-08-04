using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

internal sealed class RoleRepository : IRoleRepository
{
    private readonly AppDbContext _dbContext;

    public RoleRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<Role>> ListByIdsAsync(
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(roleIds);

        if (roleIds.Count == 0)
        {
            return [];
        }

        return await _dbContext.Set<Role>()
            .Where(role => roleIds.Contains(role.Id))
            .ToListAsync(cancellationToken);
    }

    public Task<Role?> FindPlatformRoleByNameAsync(string roleName, CancellationToken cancellationToken)
    {
        string normalizedName = Role.Normalize(roleName);

        return _dbContext.Set<Role>()
            .SingleOrDefaultAsync(
                role => role.NormalizedName == normalizedName && role.Scope == RoleScope.Platform,
                cancellationToken);
    }
}
