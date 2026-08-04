namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// What the access token must assert (AD-4). The permission set is passed in rather
/// than looked up by the adapter: the handler already resolved it, and the token
/// adapter stays free of data access.
/// </summary>
/// <param name="UserId">Becomes the <c>sub</c> claim.</param>
/// <param name="Email">Becomes the <c>email</c> claim.</param>
/// <param name="SecurityStamp">
/// Becomes the <c>security_stamp</c> claim: a stamp rotation (password change,
/// suspension) makes every outstanding token stale within one token lifetime.
/// </param>
/// <param name="Permissions">
/// The effective permission set. Hashed into <c>perm_hash</c> rather than embedded —
/// a mismatch tells the client to re-read <c>/auth/me</c> without bloating every
/// request with the full set (AD-22 §6.3).
/// </param>
/// <param name="IssuedAtUtc">Read from <c>IClock</c> at the edge and passed in (AD-40).</param>
public sealed record AccessTokenRequest(
    Guid UserId,
    string Email,
    Guid SecurityStamp,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset IssuedAtUtc);

/// <param name="Token">The compact-serialized JWT.</param>
/// <param name="ExpiresAtUtc">Absolute expiry, for the client's refresh scheduling.</param>
public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Access-token issuing seam. JWT is the implementation (AD-4), not the contract —
/// no token-library type appears in the Application layer (rule R8).
/// </summary>
public interface IAccessTokenIssuer
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}
