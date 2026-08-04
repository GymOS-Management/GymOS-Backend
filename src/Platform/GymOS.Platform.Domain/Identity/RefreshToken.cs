using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// One rotating refresh token (AD-4): only the SHA-256 hash is ever stored — the
/// raw token exists solely in the httpOnly cookie. <see cref="ReplacedByTokenHash"/>
/// forms the rotation chain that enables reuse detection: a revoked token presented
/// again revokes the whole chain.
/// </summary>
public sealed class RefreshToken : Entity, IAuditable
{
    private RefreshToken(Guid id, Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, string createdByIp)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        CreatedByIp = createdByIp;
    }

    private RefreshToken()
    {
    }

    public Guid UserId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public string CreatedByIp { get; private set; } = string.Empty;

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public string? RevokedByIp { get; private set; }

    public string? RevocationReason { get; private set; }

    /// <summary>Hash of the successor token — the rotation chain (AD-4).</summary>
    public string? ReplacedByTokenHash { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public bool IsRevoked => RevokedAtUtc is not null;

    public static RefreshToken Issue(Guid userId, string tokenHash, DateTimeOffset expiresAtUtc, string createdByIp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdByIp);
        return new RefreshToken(Guid.NewGuid(), userId, tokenHash, expiresAtUtc, createdByIp);
    }

    public bool IsActive(DateTimeOffset now) => !IsRevoked && ExpiresAtUtc > now;

    public void Revoke(DateTimeOffset now, string revokedByIp, string reason, string? replacedByTokenHash = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revokedByIp);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (IsRevoked)
        {
            throw new DomainException("The refresh token is already revoked.");
        }

        RevokedAtUtc = now;
        RevokedByIp = revokedByIp;
        RevocationReason = reason;
        ReplacedByTokenHash = replacedByTokenHash;
    }
}
