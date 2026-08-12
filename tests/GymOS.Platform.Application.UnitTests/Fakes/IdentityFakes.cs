using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>
/// In-memory stand-ins for the identity seams. Deliberately behavioral rather than
/// mocks: they hold real objects and answer real queries, so a test asserts on the
/// state a handler produced instead of on the calls it made — which keeps the tests
/// from breaking every time a handler is refactored without changing behavior.
/// </summary>
internal sealed class FakeUserRepository : IUserRepository
{
    private readonly List<User> _users = [];

    public IReadOnlyList<User> Users => _users;

    public void Seed(User user) => _users.Add(user);

    public Task<User?> FindByNormalizedEmailAsync(string normalizedEmail, CancellationToken cancellationToken) =>
        Task.FromResult(_users.SingleOrDefault(user =>
            string.Equals(user.NormalizedEmail, normalizedEmail, StringComparison.Ordinal)));

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_users.SingleOrDefault(user => user.Id == userId));

    public void Add(User user) => _users.Add(user);

    /// <summary>
    /// Records the expectation and, when <see cref="ConflictingVersion"/> matches it, simulates the
    /// store refusing the write. The real check happens in PostgreSQL, so this is the only way a
    /// handler test can cover the conflict path at all.
    /// </summary>
    public void ExpectVersion(User user, uint expectedVersion)
    {
        ExpectedVersions[user.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new ConcurrencyConflictException();
        }
    }

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Ids whose aggregate version was deliberately advanced.</summary>
    public HashSet<Guid> AggregateChanges { get; } = [];

    public void MarkAggregateChanged(User user) => AggregateChanges.Add(user.Id);

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }
}

internal sealed class FakeRefreshTokenRepository : IRefreshTokenRepository
{
    private readonly List<RefreshToken> _tokens = [];

    public IReadOnlyList<RefreshToken> Tokens => _tokens;

    public void Seed(RefreshToken token) => _tokens.Add(token);

    public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(_tokens.SingleOrDefault(token =>
            string.Equals(token.TokenHash, tokenHash, StringComparison.Ordinal)));

    public Task<RefreshToken?> FindByHashForUpdateAsync(string tokenHash, CancellationToken cancellationToken) =>
        FindByHashAsync(tokenHash, cancellationToken); // in-memory: no rows, no locks

    public Task<IReadOnlyList<RefreshToken>> ListByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshToken>>(_tokens.Where(token => token.UserId == userId).ToList());

    public void Add(RefreshToken refreshToken) => _tokens.Add(refreshToken);
}

internal sealed class FakeIdentityQueries : IIdentityQueries
{
    public Dictionary<Guid, UserProfile> Profiles { get; } = [];

    public Dictionary<Guid, IReadOnlyCollection<string>> Permissions { get; } = [];

    public Dictionary<Guid, UserSecuritySnapshot> Snapshots { get; } = [];

    public Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.TryGetValue(userId, out UserProfile? profile) ? profile : null);

    public Task<UserSecuritySnapshot?> GetSecuritySnapshotAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Snapshots.TryGetValue(userId, out UserSecuritySnapshot? snapshot) ? snapshot : null);

    public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        Task.FromResult(Permissions.TryGetValue(userId, out IReadOnlyCollection<string>? permissions)
            ? permissions
            : []);
}

/// <summary>
/// Reversible "hashing" — the point is that verification succeeds for the right input
/// and fails for the wrong one, not that it is cryptographic. <see cref="HashCallCount"/>
/// is what lets a test prove the timing-equalizing hash on an unknown email actually
/// happened, which is invisible in the response.
/// </summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    private const string Prefix = "fake-argon2:";

    public int HashCallCount { get; private set; }

    public int VerifyCallCount { get; private set; }

    public string Hash(string password)
    {
        HashCallCount++;
        return Prefix + password;
    }

    public bool Verify(string password, string passwordHash)
    {
        VerifyCallCount++;
        return string.Equals(Prefix + password, passwordHash, StringComparison.Ordinal);
    }
}

internal sealed class FakeAccessTokenIssuer : IAccessTokenIssuer
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public List<AccessTokenRequest> Requests { get; } = [];

    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        Requests.Add(request);
        return new IssuedAccessToken(
            $"access-token-{request.UserId}-{Requests.Count}",
            request.IssuedAtUtc.Add(Lifetime));
    }
}

/// <summary>Sequential, predictable tokens so a test can name the one it expects.</summary>
internal sealed class FakeRefreshTokenGenerator : IRefreshTokenGenerator
{
    private int _issued;

    public string GenerateRawToken() => $"raw-refresh-{++_issued}";

    public string ComputeHash(string rawToken) => "sha256:" + rawToken;
}

/// <summary>
/// In-memory stand-in for the set-level lock. Records what was locked so a test can assert the
/// guard serialized at all; there is nothing to serialize against in a single-threaded fake.
/// </summary>
internal sealed class FakeAdvisoryLock : IAdvisoryLock
{
    public List<string> Acquired { get; } = [];

    public Task AcquireAsync(string name, CancellationToken cancellationToken)
    {
        Acquired.Add(name);
        return Task.CompletedTask;
    }
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SaveCount { get; private set; }

    public int TransactionCount { get; private set; }

    public int DiscardCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.FromResult(0);
    }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        TransactionCount++;
        return Task.FromResult<IUnitOfWorkTransaction>(new NoOpTransaction());
    }

    // No change tracker to clear in-memory; counted so a retry test can still assert it was asked for.
    public void DiscardTrackedChanges() => DiscardCount++;

    private sealed class NoOpTransaction : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class FakeRoleRepository : IRoleRepository
{
    private readonly List<Role> _roles = [];

    public void Seed(Role role) => _roles.Add(role);

    public Task<IReadOnlyList<Role>> ListByIdsAsync(
        IReadOnlyCollection<Guid> roleIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Role>>(_roles.Where(role => roleIds.Contains(role.Id)).ToList());

    public Task<Role?> FindPlatformRoleByNameAsync(string roleName, CancellationToken cancellationToken)
    {
        string normalized = Role.Normalize(roleName);
        return Task.FromResult(_roles.SingleOrDefault(role =>
            string.Equals(role.NormalizedName, normalized, StringComparison.Ordinal)
            && role.Scope == RoleScope.Platform));
    }
}

/// <summary>
/// Read-side fake. <see cref="ActiveUsersPerRole"/> is what lets a test set up "this is the
/// only SuperAdmin left" without building a second user graph.
/// </summary>
internal sealed class FakeUserQueries : IUserQueries
{
    private readonly FakeUserRepository _users;

    /// <summary>
    /// Projects from the same in-memory aggregates the write side mutates, so a handler that
    /// returns the resource it just changed is checked against real state rather than a fixture
    /// that could disagree with it.
    /// </summary>
    public FakeUserQueries(FakeUserRepository users) => _users = users;

    public Dictionary<string, int> ActiveUsersPerRole { get; } = new(StringComparer.Ordinal);

    /// <summary>Incremented on each read, so a "version changed" assertion has something to see.</summary>
    public uint RowVersion { get; set; } = 1;

    public Task<PagedResult<UserListItem>> ListAsync(UserListFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult(PagedResult.Empty<UserListItem>(filter.Page));

    public Task<UserDetail?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        User? user = _users.Users.SingleOrDefault(candidate => candidate.Id == userId);

        return Task.FromResult(user is null
            ? null
            : new UserDetail(
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                user.Status.ToString(),
                user.SuspensionReason,
                user.LastLoginAtUtc,
                user.LockedOutUntilUtc,
                user.PasswordChangedAtUtc,
                user.CreatedAtUtc,
                user.Roles.Select(role => new RoleSummary(role.RoleId, "role", "seeded")).ToList(),
                [],
                RowVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public Task<int> CountActiveUsersInRoleAsync(string roleName, CancellationToken cancellationToken) =>
        Task.FromResult(ActiveUsersPerRole.TryGetValue(roleName, out int count) ? count : 0);
}

/// <summary>The acting caller, settable so a test can act as a specific administrator.</summary>
internal sealed class FakeCurrentUser : ICurrentUser
{
    public Guid? UserId { get; set; }

    public string? Email { get; set; }

    public bool IsAuthenticated => UserId is not null;

    public string? IpAddress { get; set; } = "198.51.100.10";

    public string? UserAgent { get; set; } = "integration-test/1.0";
}

/// <summary>Permission set the caller holds, independent of any role graph.</summary>
internal sealed class FakePermissionEvaluator : IPermissionEvaluator
{
    public HashSet<string> Granted { get; } = new(StringComparer.Ordinal);

    public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken) =>
        Task.FromResult(Granted.Contains(permission));

    public Task<IReadOnlyCollection<string>> GetPermissionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyCollection<string>>(Granted.ToList());
}

internal sealed class FakeAuditTrail : IAuditTrail
{
    public List<(string Action, string? EntityType, Guid? EntityId, IReadOnlyDictionary<string, string?>? Details, string? SubjectEmail)> Entries { get; } = [];

    public void Record(
        string action,
        string? entityType = null,
        Guid? entityId = null,
        IReadOnlyDictionary<string, string?>? details = null,
        string? subjectEmail = null) =>
        Entries.Add((action, entityType, entityId, details, subjectEmail));
}

/// <summary>In-memory activation tokens, with the one-live-link rule the real repository enforces.</summary>
internal sealed class FakeActivationTokenRepository : IUserActivationTokenRepository
{
    public List<UserActivationToken> Tokens { get; } = [];

    public Task<UserActivationToken?> FindByHashAsync(string tokenHash, CancellationToken cancellationToken) =>
        Task.FromResult(Tokens.SingleOrDefault(token => token.TokenHash == tokenHash));

    public void Add(UserActivationToken token) => Tokens.Add(token);

    public Task InvalidateOutstandingAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (UserActivationToken token in Tokens.Where(token => token.UserId == userId && !token.IsConsumed))
        {
            token.Consume(now);
        }

        return Task.CompletedTask;
    }

    public Task<int> PurgeExpiredAsync(DateTimeOffset olderThanUtc, CancellationToken cancellationToken)
    {
        int removed = Tokens.RemoveAll(token => token.ExpiresAtUtc < olderThanUtc);

        return Task.FromResult(removed);
    }
}

/// <summary>
/// Predictable tokens, so a test can assert on the value that was mailed.
///
/// <para>Deliberately not random: the production generator's job is unpredictability, which is
/// exactly what makes it useless to assert against. What these tests check is the *flow* — that the
/// token which reached the email is the one whose hash was stored.</para>
/// </summary>
internal sealed class FakeActivationTokenGenerator : IActivationTokenGenerator
{
    private int _issued;

    public string Generate() => $"raw-token-{++_issued}";

    public string ComputeHash(string rawToken) => $"hash-of-{rawToken}";
}

/// <summary>Records what would have been emailed, and can be made to fail on demand.</summary>
internal sealed class FakeActivationLinks : IActivationLinks
{
    public List<(ActivationPurpose Purpose, ActivationRecipient Recipient, string RawToken)> Sent { get; } = [];

    /// <summary>Set to simulate an unreachable mail server — the case that must not undo the account work.</summary>
    public bool FailSends { get; set; }

    public TimeSpan InvitationLifetime { get; set; } = TimeSpan.FromHours(72);

    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan LifetimeFor(ActivationPurpose purpose) =>
        purpose == ActivationPurpose.Invitation ? InvitationLifetime : PasswordResetLifetime;

    public Task<EmailSendResult> SendAsync(
        ActivationPurpose purpose,
        ActivationRecipient recipient,
        string rawToken,
        CancellationToken cancellationToken)
    {
        Sent.Add((purpose, recipient, rawToken));

        return Task.FromResult(FailSends
            ? EmailSendResult.Failed("fake transport is configured to fail")
            : EmailSendResult.Success);
    }
}
