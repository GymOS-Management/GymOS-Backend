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
    /// The rotation lookup: loads the token holding its row lock until the ambient transaction
    /// ends (<c>SELECT … FOR UPDATE</c>).
    ///
    /// <para><b>This is what makes reuse detection real.</b> Rotation is a read-then-write — see
    /// it unrevoked, revoke it, issue a successor — and without the lock two simultaneous uses of
    /// one token both pass the check and both succeed, so a replayed stolen token silently yields
    /// a second live session instead of killing the chain. Serialized here, the loser re-reads a
    /// token that is now revoked and takes the reuse-detection path, which is the behavior AD-4
    /// promises.</para>
    ///
    /// <para>Callers must hold an
    /// <see cref="GymOS.BuildingBlocks.Application.Abstractions.IUnitOfWork"/> transaction, or the
    /// lock releases at the end of the statement and buys nothing.</para>
    /// </summary>
    Task<RefreshToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>
    /// Every token of one user, tracked — the working set for chain revocation on reuse detection.
    /// Bounded by the retention job, which deletes rows that are expired or revoked past the
    /// retention window (rotation itself inserts a row per refresh; it does not replace in place).
    /// </summary>
    Task<IReadOnlyList<RefreshToken>> ListByUserAsync(Guid userId, CancellationToken cancellationToken);

    void Add(RefreshToken refreshToken);
}
