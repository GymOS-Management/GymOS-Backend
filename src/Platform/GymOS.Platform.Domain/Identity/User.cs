using GymOS.Platform.Domain.Identity.Events;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Entities;

namespace GymOS.Platform.Domain.Identity;

/// <summary>
/// A platform administrator (ARCHITECTURE.md §2.1). Gym staff and members live in
/// tenant identity, never here. All time arrives as method parameters (AD-40);
/// the <see cref="SecurityStamp"/> rotates on password change and suspension so
/// every outstanding session dies within one access-token lifetime (AD-4).
/// </summary>
public sealed class User : AggregateRoot, IAuditable
{
    private readonly List<UserRole> _roles = [];

    private User(Guid id, string email, string firstName, string lastName)
        : base(id)
    {
        Email = email;
        NormalizedEmail = Normalize(email);
        FirstName = firstName;
        LastName = lastName;
        Status = UserStatus.PendingActivation;
        SecurityStamp = Guid.NewGuid();
    }

    private User()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    /// <summary>Argon2id hash (AD-4); null until the first password is set.</summary>
    public string? PasswordHash { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public string? SuspensionReason { get; private set; }

    /// <summary>
    /// Consecutive failures since the last success. Reset on a successful sign-in and on any
    /// password change; cleared when a lockout is applied, so the counter measures the current
    /// streak rather than accumulating for the life of the account.
    /// </summary>
    public int FailedLoginAttempts { get; private set; }

    /// <summary>
    /// End of the current lockout window, or <see langword="null"/> when not locked out.
    ///
    /// Lockout is a self-expiring condition, not a lifecycle state — see
    /// <see cref="UserStatusTransitions"/> for why it is a timestamp and not a
    /// <see cref="UserStatus"/> member. Read it through <see cref="IsLockedOut"/> so the
    /// comparison against "now" stays in one place.
    /// </summary>
    public DateTimeOffset? LockedOutUntilUtc { get; private set; }

    /// <summary>
    /// Last successful sign-in.
    ///
    /// There is deliberately no companion <c>LastActivityAtUtc</c>. Recording it faithfully
    /// means writing to this row on every authenticated request, which turns every read into a
    /// write, puts every concurrent request for one administrator in contention on a single row,
    /// and defeats the request-scoped snapshot that keeps authorization to one query. The
    /// question it answers is already answerable: <c>platform.audit_logs</c> carries
    /// <c>user_id</c> and <c>created_at_utc</c>, so "when was this administrator last active"
    /// is a query against data the platform already keeps. If a live presence indicator is ever
    /// wanted, the shape is a throttled write or a read model — not a column on the aggregate.
    /// </summary>
    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    /// <summary>
    /// When the password was last set. Also the reference point for rejecting access tokens
    /// issued before a password change, alongside <see cref="SecurityStamp"/>.
    /// </summary>
    public DateTimeOffset? PasswordChangedAtUtc { get; private set; }

    /// <summary>Rotates on password change and suspension — kills all sessions (AD-4).</summary>
    public Guid SecurityStamp { get; private set; }

    public IReadOnlyCollection<UserRole> Roles => _roles.AsReadOnly();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public static User Create(string email, string firstName, string lastName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        if (!email.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainException($"'{email}' is not a valid email address.");
        }

        var user = new User(Guid.NewGuid(), email.Trim(), firstName.Trim(), lastName.Trim());
        user.Raise(new UserCreated(user.Id, user.Email));
        return user;
    }

    public static string Normalize(string email)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        return email.Trim().ToUpperInvariant();
    }

    public bool IsLockedOut(DateTimeOffset now) => LockedOutUntilUtc is { } until && until > now;

    /// <summary>
    /// Changes the display name. Email is deliberately not editable here: it is the login
    /// identity and the audit trail's denormalized key, so changing it is a separate,
    /// verified operation rather than a profile edit.
    /// </summary>
    public void UpdateProfile(string firstName, string lastName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        FirstName = firstName.Trim();
        LastName = lastName.Trim();
    }

    /// <summary>Sets or changes the password hash; activates pending users and rotates the stamp.</summary>
    public void SetPasswordHash(string passwordHash, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);

        if (Status == UserStatus.Suspended)
        {
            throw new DomainException("A suspended user's password cannot be changed.");
        }

        PasswordHash = passwordHash;
        PasswordChangedAtUtc = now;
        SecurityStamp = Guid.NewGuid();
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;

        // Setting the first password *is* activation (UserStatusTransitions). For an account
        // that is already Active this is an ordinary password change and no transition occurs.
        if (Status == UserStatus.PendingActivation)
        {
            TransitionTo(UserStatus.Active);
        }

        Raise(new UserPasswordChanged(Id));
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        LastLoginAtUtc = now;
    }

    /// <summary>Counts a failed attempt; locks the account when the threshold is hit.</summary>
    public void RecordFailedLogin(DateTimeOffset now, int maxAttempts, TimeSpan lockoutDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        FailedLoginAttempts++;

        if (FailedLoginAttempts >= maxAttempts)
        {
            LockedOutUntilUtc = now.Add(lockoutDuration);
            FailedLoginAttempts = 0;
            Raise(new UserLockedOut(Id, LockedOutUntilUtc.Value));
        }
    }

    public void Suspend(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        TransitionTo(UserStatus.Suspended);
        SuspensionReason = reason;

        // Rotating the stamp is what makes suspension immediate: outstanding access tokens stop
        // validating on their next use rather than surviving to expiry (AD-4).
        SecurityStamp = Guid.NewGuid();
        Raise(new UserSuspended(Id, reason));
    }

    public void Reactivate(DateTimeOffset now)
    {
        // Back to where the account was, not blindly to Active: an invitation that was suspended
        // before it was ever used returns to PendingActivation.
        TransitionTo(PasswordHash is null ? UserStatus.PendingActivation : UserStatus.Active);

        SuspensionReason = null;
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        Raise(new UserReactivated(Id));
    }

    /// <summary>
    /// The only place <see cref="Status"/> is assigned, so every change is checked against the
    /// transition table (<see cref="UserStatusTransitions"/>).
    /// </summary>
    private void TransitionTo(UserStatus target)
    {
        UserStatusTransitions.EnsureAllowed(Status, target);
        Status = target;
    }

    /// <summary>Idempotent: assigning an already-held role is a no-op (no event).</summary>
    public void AssignRole(Guid roleId, Guid? tenantId = null)
    {
        if (_roles.Any(role => role.RoleId == roleId && role.TenantId == tenantId))
        {
            return;
        }

        _roles.Add(new UserRole(Id, roleId, tenantId));
        Raise(new UserRoleAssigned(Id, roleId, tenantId));
    }

    public void RevokeRole(Guid roleId, Guid? tenantId = null)
    {
        UserRole? assignment = _roles.FirstOrDefault(role => role.RoleId == roleId && role.TenantId == tenantId);
        if (assignment is null)
        {
            return;
        }

        _roles.Remove(assignment);
        Raise(new UserRoleRevoked(Id, roleId, tenantId));
    }
}
