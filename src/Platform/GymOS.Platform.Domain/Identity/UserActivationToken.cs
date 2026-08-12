using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Identity;

/// <summary>Why a token was issued. The two messages ADR-53 permits, and nothing else.</summary>
public enum ActivationPurpose
{
    /// <summary>First password on an invited account, which also activates it.</summary>
    Invitation = 1,

    /// <summary>A replacement password on an account that already has one.</summary>
    PasswordReset = 2,
}

/// <summary>
/// A one-time, time-boxed permission to set one account's password.
///
/// <para><b>This is a credential, and is treated as one</b> — the same treatment M1.2 gave
/// <see cref="RefreshToken"/>, down to the generator and the hashing. The raw token exists only in
/// the link that was emailed; the database holds its SHA-256, so a leaked backup does not hand over
/// every pending account.</para>
///
/// <para>Single-use and time-boxed are enforced <em>here</em> rather than in the handler that reads
/// it: a second caller of <see cref="Consume"/> must be refused however it got there.</para>
/// </summary>
public sealed class UserActivationToken : Entity
{
    private UserActivationToken(
        Guid id,
        Guid userId,
        string tokenHash,
        ActivationPurpose purpose,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset createdAtUtc,
        Guid? createdByUserId)
        : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        Purpose = purpose;
        ExpiresAtUtc = expiresAtUtc;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
    }

    private UserActivationToken()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>SHA-256 of the raw token, lower-case hex — <see cref="RefreshToken.TokenHash"/>'s shape.</summary>
    public string TokenHash { get; private set; } = string.Empty;

    public ActivationPurpose Purpose { get; private set; }

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConsumedAtUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    /// <summary>
    /// The administrator who caused it. Nullable and cleared with that account: provenance, not a
    /// dependency — the <c>PlanPriceId</c> precedent, so losing the author never invalidates the link.
    /// </summary>
    public Guid? CreatedByUserId { get; private set; }

    public bool IsConsumed => ConsumedAtUtc is not null;

    public bool IsUsable(DateTimeOffset now) => !IsConsumed && ExpiresAtUtc > now;

    public static UserActivationToken Issue(
        Guid userId,
        string tokenHash,
        ActivationPurpose purpose,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset now,
        Guid? createdByUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHash);

        if (expiresAtUtc <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                "An activation token that expires at or before it is issued can never be used.");
        }

        return new UserActivationToken(
            Guid.NewGuid(), userId, tokenHash, purpose, expiresAtUtc, now, createdByUserId);
    }

    /// <summary>
    /// Spends the token.
    ///
    /// <para><b>Called before the new password is validated</b>, deliberately. Consuming it only on
    /// success would leave a link replayable for its whole lifetime by anyone willing to submit a
    /// password the policy rejects — the attempt costs them nothing and the link survives it.</para>
    /// </summary>
    /// <returns>False when it was already spent or has expired; the caller reports both identically.</returns>
    public bool Consume(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            return false;
        }

        ConsumedAtUtc = now;

        return true;
    }
}
