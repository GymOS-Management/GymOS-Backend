using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.Identity.Abstractions;

/// <summary>
/// Write-side access to activation tokens. <b>Lookups are by hash only</b> — the raw token is never a
/// query parameter, the same rule refresh tokens follow (AD-4).
/// </summary>
public interface IUserActivationTokenRepository
{
    Task<UserActivationToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken);

    void Add(UserActivationToken token);

    /// <summary>
    /// Spends every unconsumed token a user holds, whatever its purpose.
    ///
    /// <para>Called before issuing a new one, so <b>a user has at most one live link</b>. Without it,
    /// re-sending an invitation leaves the first link working — and "I re-sent it because the first
    /// one might have been intercepted" stops being true the moment two are live.</para>
    /// </summary>
    Task InvalidateOutstandingAsync(
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Deletes tokens that are consumed or long past expiry, for <c>RetentionJob</c>.
    ///
    /// <para>A credential table that only grows is the defect that job already exists to prevent —
    /// the same reasoning that put the refresh-token purge there.</para>
    /// </summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken);
}
