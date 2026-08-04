using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class UserQueries : IUserQueries
{
    /// <summary>
    /// Sortable columns, by request value. An allowlist rather than dynamic expression
    /// building: <c>?sortBy=</c> is caller-controlled input, and mapping it to a property
    /// by reflection or string interpolation is how a sort parameter becomes an injection
    /// point or an accidental full-table scan.
    /// </summary>
    private static readonly string[] SortableFields = ["email", "lastName", "status", "lastLoginAtUtc", "createdAtUtc"];

    private readonly AppDbContext _dbContext;
    private readonly IClock _clock;

    public UserQueries(AppDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<PagedResult<UserListItem>> ListAsync(
        UserListFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        IQueryable<User> query = _dbContext.Set<User>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            // An unparseable status yields no rows rather than an error: a stale filter
            // chip in a bookmarked URL should show an empty list, not a 400.
            if (!Enum.TryParse(filter.Status, ignoreCase: true, out UserStatus status))
            {
                return PagedResult.Empty<UserListItem>(filter.Page);
            }

            query = query.Where(user => user.Status == status);
        }

        if (filter.Page.Search is { } search)
        {
            string pattern = $"%{search}%";
            query = query.Where(user =>
                EF.Functions.ILike(user.Email, pattern) ||
                EF.Functions.ILike(user.FirstName, pattern) ||
                EF.Functions.ILike(user.LastName, pattern));
        }

        int totalCount = await query.CountAsync(cancellationToken);
        DateTimeOffset now = _clock.UtcNow;

        var page = await Sort(query, filter.Page)
            .Skip(filter.Page.Skip)
            .Take(filter.Page.PageSize)
            .Select(user => new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Status,
                user.LastLoginAtUtc,
                user.LockedOutUntilUtc,
                Roles = _dbContext.Set<UserRole>()
                    .Where(assignment => assignment.UserId == user.Id)
                    .Join(_dbContext.Set<Role>(), a => a.RoleId, role => role.Id, (_, role) => role.Name)
                    .ToList(),
            })
            .ToListAsync(cancellationToken);

        var items = page
            .Select(user => new UserListItem(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Status.ToString(),
                user.LastLoginAtUtc,
                user.LockedOutUntilUtc > now,
                user.Roles))
            .ToList();

        return new PagedResult<UserListItem>(items, filter.Page.Page, filter.Page.PageSize, totalCount);
    }

    public async Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
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
                user.SuspensionReason,
                user.LastLoginAtUtc,
                user.LockedOutUntilUtc,
                user.PasswordChangedAtUtc,
                user.CreatedAtUtc,
                Roles = _dbContext.Set<UserRole>()
                    .Where(assignment => assignment.UserId == user.Id)
                    .Join(
                        _dbContext.Set<Role>(),
                        a => a.RoleId,
                        role => role.Id,
                        (_, role) => new RoleSummary(role.Id, role.Name, role.Description))
                    .ToList(),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (projection is null)
        {
            return null;
        }

        // The resolved permission set is shown alongside the roles: "which roles" is not
        // the question an administrator is actually asking when they open this page.
        var permissions = await _dbContext.Set<UserRole>()
            .AsNoTracking()
            .Where(assignment => assignment.UserId == userId)
            .Join(_dbContext.Set<RolePermission>(), a => a.RoleId, grant => grant.RoleId, (_, grant) => grant.PermissionId)
            .Join(_dbContext.Set<Permission>(), id => id, permission => permission.Id, (_, permission) => permission.Key)
            .Distinct()
            .OrderBy(key => key)
            .ToListAsync(cancellationToken);

        return new UserDetail(
            projection.Id,
            projection.Email,
            projection.FirstName,
            projection.LastName,
            projection.Status.ToString(),
            projection.SuspensionReason,
            projection.LastLoginAtUtc,
            projection.LockedOutUntilUtc,
            projection.PasswordChangedAtUtc,
            projection.CreatedAtUtc,
            projection.Roles,
            permissions);
    }

    public async Task<int> CountActiveUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
    {
        string normalizedName = Role.Normalize(roleName);

        return await _dbContext.Set<UserRole>()
            .AsNoTracking()
            .Where(assignment => _dbContext.Set<Role>()
                .Any(role => role.Id == assignment.RoleId
                    && role.NormalizedName == normalizedName
                    && role.Scope == RoleScope.Platform))
            .Where(assignment => _dbContext.Set<User>()
                .Any(user => user.Id == assignment.UserId && user.Status == UserStatus.Active))
            .Select(assignment => assignment.UserId)
            .Distinct()
            .CountAsync(cancellationToken);
    }

    private static IQueryable<User> Sort(IQueryable<User> query, PageRequest page) =>
        (page.SortBy is not null && SortableFields.Contains(page.SortBy, StringComparer.OrdinalIgnoreCase)
            ? page.SortBy.ToLowerInvariant() switch
            {
                "email" => page.Descending
                    ? query.OrderByDescending(user => user.Email)
                    : query.OrderBy(user => user.Email),
                "lastname" => page.Descending
                    ? query.OrderByDescending(user => user.LastName)
                    : query.OrderBy(user => user.LastName),
                "status" => page.Descending
                    ? query.OrderByDescending(user => user.Status)
                    : query.OrderBy(user => user.Status),
                "lastloginatutc" => page.Descending
                    ? query.OrderByDescending(user => user.LastLoginAtUtc)
                    : query.OrderBy(user => user.LastLoginAtUtc),
                _ => page.Descending
                    ? query.OrderByDescending(user => user.CreatedAtUtc)
                    : query.OrderBy(user => user.CreatedAtUtc),
            }
            : query.OrderBy(user => user.Email))
        // Id breaks ties: without a total order, two pages of an ambiguous sort can repeat
        // or skip rows, and the client sees a list that shuffles as it pages.
        .ThenBy(user => user.Id);
}
