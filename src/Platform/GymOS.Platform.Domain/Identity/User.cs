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

    public int FailedLoginAttempts { get; private set; }

    public DateTimeOffset? LockedOutUntilUtc { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

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

        if (Status == UserStatus.PendingActivation)
        {
            Status = UserStatus.Active;
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

        if (Status == UserStatus.Suspended)
        {
            throw new DomainException("The user is already suspended.");
        }

        Status = UserStatus.Suspended;
        SuspensionReason = reason;
        SecurityStamp = Guid.NewGuid();
        Raise(new UserSuspended(Id, reason));
    }

    public void Reactivate(DateTimeOffset now)
    {
        if (Status != UserStatus.Suspended)
        {
            throw new DomainException("Only a suspended user can be reactivated.");
        }

        Status = PasswordHash is null ? UserStatus.PendingActivation : UserStatus.Active;
        SuspensionReason = null;
        FailedLoginAttempts = 0;
        LockedOutUntilUtc = null;
        Raise(new UserReactivated(Id));
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
