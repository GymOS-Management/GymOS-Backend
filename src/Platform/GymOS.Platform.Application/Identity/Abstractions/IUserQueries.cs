using GymOS.BuildingBlocks.Application.Models;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>One row of the platform administrators list.</summary>
public sealed record UserListItem(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    DateTimeOffset? LastLoginAtUtc,
    bool IsLockedOut,
    IReadOnlyCollection<string> Roles);

/// <summary>
/// A single administrator, with the operational detail the console shows.
/// </summary>
/// <param name="RowVersion">
/// Opaque version token. Echo it in <c>If-Match</c> when updating so the write is refused if
/// someone else changed the account in the meantime (ARCHITECTURE.md §3). Also returned as the
/// response <c>ETag</c>; the body copy exists for clients that would rather not read headers.
/// </param>
public sealed record UserDetail(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    string? SuspensionReason,
    DateTimeOffset? LastLoginAtUtc,
    DateTimeOffset? LockedOutUntilUtc,
    DateTimeOffset? PasswordChangedAtUtc,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<RoleSummary> Roles,
    IReadOnlyCollection<string> Permissions,
    string RowVersion);

/// <param name="Status">Optional exact-match filter on <c>UserStatus</c>, by name.</param>
public sealed record UserListFilter(PageRequest Page, string? Status);

/// <summary>
/// Read side of the platform-users surface (AD-3): projections, no tracking, no aggregate
/// loading.
/// </summary>
public interface IUserQueries
{
    Task<PagedResult<UserListItem>> ListAsync(UserListFilter filter, CancellationToken cancellationToken);

    Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// How many <b>active</b> users hold the named platform role. Exists for one reason:
    /// the last SuperAdmin must not be demotable or suspendable, and that is a rule about
    /// the set of users — not about any one of them — so no aggregate can enforce it.
    /// </summary>
    Task<int> CountActiveUsersInRoleAsync(string roleName, CancellationToken cancellationToken);
}
