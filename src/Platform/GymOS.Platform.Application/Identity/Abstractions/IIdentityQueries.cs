namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>A user's identity as the API reports it — a projection, never a tracked aggregate.</summary>
public sealed record UserProfile(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyCollection<string> Roles);

/// <summary>What the bearer handler needs on every request: is this still the same account, and is it active.</summary>
/// <param name="SecurityStamp">
/// Compared against the token's <c>security_stamp</c> claim. A mismatch means the account
/// changed underneath the token — password changed, account suspended — and the token is
/// refused on the spot rather than honored until it expires.
/// </param>
public sealed record UserSecurityIdentity(Guid UserId, Guid SecurityStamp, string Status);

/// <summary>
/// The identity plus the permissions the caller holds <em>for the acting tenant</em> —
/// platform grants ∪ grants naming <see cref="TenantId"/> (grants spec D3). With a null tenant,
/// exactly the platform grants.
/// </summary>
public sealed record UserSecuritySnapshot(
    Guid UserId,
    Guid? TenantId,
    Guid SecurityStamp,
    string Status,
    IReadOnlyCollection<string> Permissions);

/// <summary>
/// Per-request memo of the caller's security reads (AD-22). Identity is cached once per user;
/// permissions once per (user, tenant) — so the stamp check during authentication, which knows no
/// tenant yet, never pays for a permission set it does not use, and a permission set computed for one
/// gym can never answer for another (grants spec D4).
/// </summary>
public interface ICallerSnapshot
{
    Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken);

    Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the identity area (AD-3: projections read directly, no repository
/// indirection). Implementations project with no change tracking.
/// </summary>
public interface IIdentityQueries
{
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><see langword="null"/> when the user no longer exists.</summary>
    Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Platform grants only. Describes the user, not a request — for sessions, profiles and the user console.</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Platform grants ∪ grants naming <paramref name="tenantId"/>; with null, exactly the platform grants.</summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken cancellationToken);
}
