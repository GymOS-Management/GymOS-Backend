using System.Net;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests.Tenancy;

/// <summary>
/// D2 (R14): the tenant comes from the route, checked against the caller's grants. Every route
/// exercised here already exists — this suite proves the resolution is inert for the whole platform
/// as it stands today, and correct for the one case nothing else has exercised: a caller whose grant
/// is for a *different* gym than the one in the route.
/// </summary>
/// <remarks>
/// <para><b>Why the existing suite is inert, stated precisely.</b> It is tempting to say "because
/// those tests call as a SuperAdmin", and that is not the reason — several of them call as
/// <c>SupportAgent</c> or <c>BillingAdmin</c> against gym-routed endpoints. The actual invariant is
/// that <b>every grant any role can hold today is platform-wide</b>: <c>SetUserRolesCommandHandler</c>
/// rejects a non-platform-scoped role and only ever writes <c>UserRole.TenantId = null</c>, so there
/// is currently no route through the API by which a gym-scoped grant can be created at all. The test
/// below that needs one writes the <c>UserRole</c> row through the DbContext for exactly that reason.
/// </para>
/// <para>That invariant is the first thing the Members increment invalidates, and when it does, this
/// suite's inertness stops being free — the gym-grant branch of the middleware becomes reachable
/// through the API for the first time.</para>
/// </remarks>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class TenantResolutionTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public TenantResolutionTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("tenant_resolution");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task ARouteWithNoGymIdLeavesTheRequestInPlatformScope()
    {
        // Every endpoint built so far is platform-scope, and must stay exactly as it was.
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/v1/plans", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ARouteWithAGymIdSetsThatTenantForTheRequest()
    {
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "tenant-route-gym");

        // A SuperAdmin holds a platform-wide grant, so it may act for any gym — and still acts for ONE.
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AGymTheCallerHasNoGrantForIsNotFound_NotForbidden()
    {
        // 404, deliberately. "You may not see this gym" and "this gym does not exist" must be
        // indistinguishable, or the status code becomes an oracle for enumerating tenants.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "someone-elses-gym");
        HttpClient scoped = await SignedInWithGrantForAsync(Guid.NewGuid()); // a grant for a DIFFERENT gym

        using HttpResponseMessage response = await scoped.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AMalformedGymIdIsNotFound_AndSetsNoTenant()
    {
        // Not evidence that the middleware handles malformed input: every gym route declares
        // `{gymId:guid}`, so ASP.NET's own routing rejects a non-Guid segment with 404 before any
        // endpoint or route-value-reading middleware ever sees the request. The assertion is still
        // worth pinning — a client hitting this path must keep getting 404 — but the 404 here is
        // routing's, not TenantResolutionMiddleware's. The middleware's own `Guid.TryParse` branch
        // for a present-but-malformed `gymId` is therefore currently unreachable through HTTP; it is
        // kept as defensive handling for a future route that omits the `:guid` constraint.
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/gyms/not-a-guid", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a user holding a grant for exactly one gym and returns a client signed in as them.
    /// </summary>
    /// <remarks>
    /// <c>SetUserRolesCommand</c> (the handler behind <c>PUT /users/{id}/roles</c>, and so behind
    /// <see cref="PlatformApiHelpers.SetRolesAsync"/>) refuses any role whose <c>Scope</c> is not
    /// <see cref="RoleScope.Platform"/>, and the assignment it writes always carries a null
    /// <c>TenantId</c> (<c>User.AssignRole(roleId)</c>, no second argument) — there is today no API
    /// surface that can produce a <see cref="UserRole"/> row scoped to one gym. So this writes the
    /// assignment directly through the DbContext, the same way <c>GymFeatureApiTests</c> and
    /// <c>GymApiTests</c> already write a role directly when the API has no route for the precondition
    /// they need.
    ///
    /// The account still needs a real, working password to sign in with, and the API offers no way for
    /// one administrator to set another's — so, exactly as <see cref="PlatformApiHelpers.SignedInAsync"/>
    /// does, a second host is booted whose SuperAdmin seed targets this address. Seeding grants
    /// SuperAdmin — a platform-wide (null-<c>TenantId</c>) grant — which would make every gym reachable
    /// regardless of what this method does next, so that grant is revoked before the gym-scoped one is
    /// written.
    /// </remarks>
    private async Task<HttpClient> SignedInWithGrantForAsync(Guid gymId)
    {
        const string Password = "grant-holder-integration-password";
        string email = $"grant-holder-{Guid.NewGuid():N}@gymos.test";

        await using AuthenticationHost seedHost = AuthenticationHost.Create(
            _host.ConnectionString,
            settings =>
            {
                settings["Seed:SuperAdmin:Email"] = email;
                settings["Seed:SuperAdmin:Password"] = Password;
            });

        using HttpClient seedClient = seedHost.CreateClient();
        using HttpResponseMessage ready = await seedClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        await using AppDbContext context = _host.CreateDbContext();

        Guid supportAgentRoleId = await context.Set<Role>()
            .Where(role => role.NormalizedName == Role.Normalize(SystemRoles.SupportAgent))
            .Select(role => role.Id)
            .SingleAsync();

        User user = await context.Set<User>()
            .Include(candidate => candidate.Roles)
            .SingleAsync(candidate => candidate.NormalizedEmail == User.Normalize(email));

        // Drop the platform-wide grant seeding just assigned, then add a grant for exactly one gym —
        // the shape the test needs and the API cannot express.
        foreach (UserRole assignment in user.Roles.Where(assignment => assignment.TenantId is null).ToList())
        {
            user.RevokeRole(assignment.RoleId);
        }

        user.AssignRole(supportAgentRoleId, gymId);
        await context.SaveChangesAsync();

        HttpClient client = _host.CreateClient();
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(client, await AuthApi.ReadAccessTokenAsync(signIn));

        return client;
    }
}
