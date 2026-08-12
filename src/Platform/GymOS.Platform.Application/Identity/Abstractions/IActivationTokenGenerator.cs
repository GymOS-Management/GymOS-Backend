namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Makes and hashes activation tokens (ADR-53).
///
/// <para>Its own seam rather than a reuse of <see cref="IRefreshTokenGenerator"/>, even though the
/// two would compute the same bytes today. They answer different questions — one mints a rotating
/// session credential read from a cookie, the other a single-use link mailed to a person — and a
/// shared implementation means a change made for one silently changes the security properties of the
/// other. That is the mistake M4.1 recorded when it un-shared a helper for exactly this reason.</para>
/// </summary>
public interface IActivationTokenGenerator
{
    /// <summary>A URL-safe token with enough entropy that guessing is not a threat model.</summary>
    string Generate();

    /// <summary>The value stored in the database. The raw token is never persisted.</summary>
    string ComputeHash(string rawToken);
}
