using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Write-side access to the <see cref="User"/> aggregate. A repository exists here
/// because User carries real invariants (lockout, activation, stamp rotation) that
/// must be loaded and saved as a whole (AD-3); read-only projections do not go
/// through it.
/// </summary>
public interface IUserRepository
{
    /// <summary>
    /// Loads the aggregate by normalized email (<see cref="User.Normalize"/>),
    /// tracked for mutation. <see langword="null"/> when no such user exists.
    /// </summary>
    Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken);

    /// <summary>Loads the aggregate by id, tracked for mutation.</summary>
    Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken);

    void Add(User user);
}
