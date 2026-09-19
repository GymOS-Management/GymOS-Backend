using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Infrastructure.Tenancy;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Infrastructure.Identity;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// The tenant-aware half of authorization (gym-scoped-grants spec D3/D4). These are the cheap proofs
/// of the shape; the escalation itself is proved end to end in <c>TenantResolutionTests</c>.
/// </summary>
public sealed class PermissionEvaluatorTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GymA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid GymB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task APlatformRouteEvaluatesPlatformGrantsOnly()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        var evaluator = Evaluator(queries, new TenantContext(), out _);

        IReadOnlyCollection<string> held = await evaluator.GetPermissionsAsync(CancellationToken.None);

        Assert.Equal(["audit.read"], held);
        Assert.Equal([(UserId, (Guid?)null)], queries.PermissionReads);
    }

    [Fact]
    public async Task AGymRouteEvaluatesTheUnionForThatGym()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        queries.ForTenant[GymB] = ["billing.read"];
        var tenant = new TenantContext();
        tenant.SetTenant(GymA);
        var evaluator = Evaluator(queries, tenant, out _);

        IReadOnlyCollection<string> held = await evaluator.GetPermissionsAsync(CancellationToken.None);

        Assert.Equal(["audit.read", "gyms.read"], held);
        Assert.DoesNotContain("billing.read", held);
    }

    [Fact]
    public async Task ASuspendedUserHoldsNothingWhateverTheTenant()
    {
        var queries = new RecordingIdentityQueries { Status = "Suspended" };
        queries.Platform.Add("audit.read");
        queries.ForTenant[GymA] = ["gyms.read"];
        var tenant = new TenantContext();
        tenant.SetTenant(GymA);
        var evaluator = Evaluator(queries, tenant, out _);

        Assert.Empty(await evaluator.GetPermissionsAsync(CancellationToken.None));
        Assert.False(await evaluator.HasPermissionAsync("gyms.read", CancellationToken.None));
    }

    [Fact]
    public async Task AnAnonymousCallerHoldsNothingAndReadsNothing()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var evaluator = new PermissionEvaluator(new StubCurrentUser(null), new TenantContext(), new CallerSnapshot(queries));

        Assert.Empty(await evaluator.GetPermissionsAsync(CancellationToken.None));
        Assert.Equal(0, queries.IdentityReads);
        Assert.Empty(queries.PermissionReads);
    }

    [Fact]
    public async Task TheIdentityIsReadOnceForTheRequest()
    {
        // The bearer handler reads identity during authentication; authorization reads the snapshot
        // later. One user row, not two.
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var snapshot = new CallerSnapshot(queries);

        Assert.NotNull(await snapshot.GetIdentityAsync(UserId, CancellationToken.None));
        Assert.NotNull(await snapshot.GetAsync(UserId, GymA, CancellationToken.None));

        Assert.Equal(1, queries.IdentityReads);
        Assert.Single(queries.PermissionReads);
    }

    [Fact]
    public async Task PermissionsAreCachedPerUserAndTenant()
    {
        var queries = new RecordingIdentityQueries { Status = "Active" };
        var snapshot = new CallerSnapshot(queries);

        await snapshot.GetAsync(UserId, GymA, CancellationToken.None);
        await snapshot.GetAsync(UserId, GymA, CancellationToken.None);
        Assert.Single(queries.PermissionReads);

        // A different tenant in the same request is a different set. It cannot happen through the
        // pipeline today; it is what stops gym A's set answering for gym B if that ever changes.
        await snapshot.GetAsync(UserId, GymB, CancellationToken.None);
        Assert.Equal([(UserId, (Guid?)GymA), (UserId, (Guid?)GymB)], queries.PermissionReads);
    }

    [Fact]
    public async Task AnUnknownUserIsNullWithoutAPermissionRead()
    {
        var queries = new RecordingIdentityQueries { Status = "Active", UserExists = false };
        var snapshot = new CallerSnapshot(queries);

        Assert.Null(await snapshot.GetAsync(UserId, GymA, CancellationToken.None));
        Assert.Empty(queries.PermissionReads);
    }

    private static PermissionEvaluator Evaluator(RecordingIdentityQueries queries, TenantContext tenant, out CallerSnapshot snapshot)
    {
        snapshot = new CallerSnapshot(queries);
        return new PermissionEvaluator(new StubCurrentUser(UserId), tenant, snapshot);
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public StubCurrentUser(Guid? userId) => UserId = userId;

        public Guid? UserId { get; }

        public string? Email => UserId is null ? null : "operator@gymos.test";

        public bool IsAuthenticated => UserId is not null;

        public string? IpAddress => "127.0.0.1";

        public string? UserAgent => "xunit";
    }

    private sealed class RecordingIdentityQueries : IIdentityQueries
    {
        public string Status { get; init; } = "Active";

        public bool UserExists { get; init; } = true;

        public HashSet<string> Platform { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, string[]> ForTenant { get; } = [];

        public int IdentityReads { get; private set; }

        public List<(Guid UserId, Guid? TenantId)> PermissionReads { get; } = [];

        public Task<UserProfile?> GetProfileAsync(Guid userId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<UserSecurityIdentity?> GetSecurityIdentityAsync(Guid userId, CancellationToken cancellationToken)
        {
            IdentityReads++;
            return Task.FromResult(UserExists ? new UserSecurityIdentity(userId, Guid.NewGuid(), Status) : null);
        }

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, CancellationToken cancellationToken) =>
            GetEffectivePermissionsAsync(userId, null, cancellationToken);

        public Task<IReadOnlyCollection<string>> GetEffectivePermissionsAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken)
        {
            PermissionReads.Add((userId, tenantId));
            IEnumerable<string> scoped = tenantId is { } acting && ForTenant.TryGetValue(acting, out string[]? held) ? held : [];
            return Task.FromResult<IReadOnlyCollection<string>>(
                Platform.Union(scoped, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal).ToList());
        }
    }
}
