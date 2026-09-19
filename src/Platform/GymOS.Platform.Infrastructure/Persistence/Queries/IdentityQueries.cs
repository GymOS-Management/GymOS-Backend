using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

/// <summary>
/// Read-side identity projections (AD-3): no tracking, no aggregate loading, shaped
/// exactly as the API returns them.
/// </summary>
internal sealed class IdentityQueries : IIdentityQueries
{
    private readonly AppDbContext _dbContext;

    public IdentityQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        // Role names come from a correlated subquery rather than an Include: the join
        // tables carry no navigation properties by design (they are aggregate
        // internals), and one query beats two round-trips for a per-request endpoint.
        var projection = await _dbContext.Set<User>()
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Status,
                Roles = _dbContext.Set<UserRole>()
                    .Where(assignment => assignment.UserId == user.Id)
                    .Join(
                        _dbContext.Set<Role>(),
                        assignment => assignment.RoleId,
                        role => role.Id,
                        (_, role) => role.Name)
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        return projection is null
            ? null
            : new UserProfile(
                projection.Id,
                projection.Email,
                projection.FirstName,
                projection.LastName,
                projection.Status.ToString(),
                projection.Roles);
    }

    public async Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Set<User>()
            .AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new { candidate.SecurityStamp, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);

        return user is null
            ? null
            : new UserSecurityIdentity(userId, user.SecurityStamp, user.Status.ToString());
    }

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        GetEffectivePermissionsAsync(userId, tenantId: null, cancellationToken);

    public async Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        // Grants spec D3: platform grants (TenantId null) ∪ grants naming the acting tenant. With no
        // acting tenant the second disjunct collapses to `TenantId == null` — exactly the platform set,
        // by construction rather than by a special case. Before H1 this filtered on UserId alone, which
        // would have handed a gym-scoped role's permissions to every route the day such a grant existed.
        return await _dbContext.Set<UserRole>()
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId
                && (assignment.TenantId == null || assignment.TenantId == tenantId))
            .Join(
                _dbContext.Set<RolePermission>(),
                assignment => assignment.RoleId,
                grant => grant.RoleId,
                (_, grant) => grant.PermissionId)
            .Join(
                _dbContext.Set<Permission>(),
                permissionId => permissionId,
                permission => permission.Id,
                (_, permission) => permission.Key)
            .Distinct()
            .OrderBy(key => key)
            .ToListAsync(cancellationToken);
    }
}
