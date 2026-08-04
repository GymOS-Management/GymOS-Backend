namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>A user's identity as the API reports it — a projection, never a tracked aggregate.</summary>
public sealed record UserProfile(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyCollection<string> Roles);

/// <summary>
/// Everything an authenticated request needs to decide whether the caller may proceed,
/// in one read (AD-4/AD-22). Bundled rather than fetched piecemeal because every
/// authenticated request needs all of it, and three round-trips per request to answer one
/// question is a cost that compounds.
/// </summary>
/// <param name="SecurityStamp">
/// Compared against the token's <c>security_stamp</c> claim. A mismatch means the account
/// changed underneath the token — password changed, account suspended — and the token is
/// refused on the spot rather than honored until it expires.
/// </param>
public sealed record UserSecuritySnapshot(
    Guid UserId,
    Guid SecurityStamp,
    string Status,
    IReadOnlyCollection<string> Permissions);

/// <summary>
/// The caller's security snapshot, resolved once per request.
///
/// Three places in one request need it — the bearer-token validator checks the security
/// stamp, the endpoint policy checks a permission, and the handler pipeline checks it again
/// — and reading the same rows three times to answer one question is waste that scales with
/// traffic. Implementations memoize for the request's lifetime and no longer, so a
/// permission change never has to wait on a cache.
/// </summary>
public interface ICallerSnapshot
{
    Task<UserSecuritySnapshot?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

/// <summary>
/// Read side of the identity area (AD-3: projections read directly, no repository
/// indirection). Implementations project with no change tracking.
/// </summary>
public interface IIdentityQueries
{
    Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary><see langword="null"/> when the user no longer exists.</summary>
    Task<UserSecuritySnapshot?> GetSecuritySnapshotAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// The union of permission keys granted by every role the user holds (AD-22 —
    /// roles are nothing but named permission bundles).
    ///
    /// This is the *first* term of the AD-22 §6.2 effective-access formula. The
    /// feature-flag and tenant-status terms join it when those modules land;
    /// platform scope skips the tenant term by definition.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken);
}
