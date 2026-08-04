namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Password hashing seam (AD-4: Argon2id, parameters from configuration). The hash
/// string is self-describing — it carries the parameters it was produced with — so
/// raising cost parameters never invalidates existing hashes.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a plaintext password with the currently configured parameters.</summary>
    string Hash(string password);

    /// <summary>
    /// Verifies a plaintext password against a stored hash in constant time.
    /// A malformed or unrecognized hash verifies as <see langword="false"/> —
    /// never an exception, so a corrupt row cannot be distinguished from a wrong
    /// password by an attacker.
    /// </summary>
    bool Verify(string password, string passwordHash);
}
