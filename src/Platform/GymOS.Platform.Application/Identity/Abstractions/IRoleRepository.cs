using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Role lookups for the write side. Reads that only display roles go through
/// <see cref="IRoleQueries"/>; this exists for the commands that must validate a role
/// exists and is assignable before granting it.
/// </summary>
public interface IRoleRepository
{
    Task<IReadOnlyList<Role>> ListByIdsAsync(IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);

    Task<Role?> FindPlatformRoleByNameAsync(string roleName, CancellationToken cancellationToken);
}
