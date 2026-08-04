namespace GymOS.Platform.Application.Identity.Abstractions;

public sealed record RoleSummary(Guid Id, string Name, string Description);

/// <param name="IsSystem">Seeded roles: grants are code-canonical and cannot be edited or deleted.</param>
/// <param name="AssignedUserCount">
/// How many users hold it — the console needs this before anyone edits a role, since a
/// grant change is not visibly dangerous until you know how many people it reaches.
/// </param>
public sealed record RoleDetail(
    Guid Id,
    string Name,
    string Description,
    string Scope,
    bool IsSystem,
    int AssignedUserCount,
    IReadOnlyCollection<string> Permissions);

public interface IRoleQueries
{
    /// <summary>Platform-scoped roles. Tenant roles arrive with the gym module.</summary>
    Task<IReadOnlyList<RoleDetail>> ListPlatformRolesAsync(CancellationToken cancellationToken);

    Task<RoleDetail?> GetAsync(Guid roleId, CancellationToken cancellationToken);
}
