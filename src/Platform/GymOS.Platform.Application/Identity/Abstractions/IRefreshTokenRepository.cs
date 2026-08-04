using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Write-side access to refresh tokens. Lookups are by hash only — the raw token is
/// never a query parameter anywhere in the system (AD-4).
/// </summary>
public interface IRefreshTokenRepository
{
    Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Every token of one user, tracked — the working set for chain revocation on
    /// reuse detection. Bounded in practice: rotation replaces rather than
    /// accumulates, and expired rows are purged by the retention job.
    /// </summary>
    Task<IReadOnlyList<RefreshToken>> ListByUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(RefreshToken refreshToken);
}
