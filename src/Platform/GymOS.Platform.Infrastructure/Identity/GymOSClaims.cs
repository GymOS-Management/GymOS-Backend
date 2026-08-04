namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// The non-standard claim names GymOS tokens carry (AD-4). Constants because both the
/// issuer and the endpoints that read them must agree exactly; a typo in a claim name
/// is an authorization bug that no test of either side alone would catch.
/// </summary>
public static class GymOSClaims
{
    /// <summary>
    /// RFC 7519 <c>sub</c>: the user id. Kept as the short registered name on both
    /// sides — inbound claim mapping to the legacy WS-Federation URIs is switched off
    /// at the validator, so what the token says is what the endpoint reads.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>RFC 7519 <c>email</c>, short form for the same reason as <see cref="Subject"/>.</summary>
    public const string Email = "email";

    /// <summary>
    /// Rotated on password change and suspension. Present so a future pipeline stage
    /// can reject tokens whose stamp no longer matches the stored one (M1.3).
    /// </summary>
    public const string SecurityStamp = "security_stamp";

    /// <summary>
    /// Fingerprint of the effective permission set. A client whose cached permissions
    /// hash differently knows to re-read <c>/auth/me</c>, so permission changes surface
    /// without shipping the whole set in every request (AD-22 §6.3).
    /// </summary>
    public const string PermissionsHash = "perm_hash";
}
