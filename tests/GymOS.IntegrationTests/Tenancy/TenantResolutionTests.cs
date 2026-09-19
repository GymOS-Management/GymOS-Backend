using System.Net;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class TenantResolutionTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public TenantResolutionTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("tenant_resolution");
        _host = AuthenticationHost.Create(
            connectionString,
            configureServices: services =>
                services.AddSingleton<IStartupFilter, ResolvedTenantProbeStartupFilter>());

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
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task ARouteWithNoGymIdLeavesTheRequestInPlatformScope()
    {
        // Every endpoint built so far is platform-scope, and must stay exactly as it was.
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/v1/plans", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // And the tenant really was left unset — not merely "the response was 200 anyway".
        Assert.Equal(ResolvedTenantProbeStartupFilter.NoTenant, ResolvedTenant(response));
    }

    [Fact]
    public async Task ARouteWithAGymIdSetsThatTenantForTheRequest()
    {
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "tenant-route-gym");

        // A SuperAdmin holds a platform-wide grant, so it may act for any gym — and still acts for ONE.
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The assertion this test existed without until the increment's final review. A 200 proves only
        // that the middleware let the request through; deleting `SetTenant(gymId)` outright left it
        // green. The property the Members increment leans on hardest — the gym in the URL is the gym
        // whose rows you get — starts here, with the resolved tenant having actually reached
        // ITenantContext during the request.
        // Since H1 this test also carries the gym-scoped-grants spec's §6 claim: the middleware now
        // runs before authorization, so a tenant observed here proves route values are populated that
        // early in the pipeline.
        Assert.Equal(gym.ToString(), ResolvedTenant(response));
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

        // And it stopped before setting anything: a refused caller must not leave a tenant behind for
        // whatever runs next in the same scope.
        Assert.Equal(ResolvedTenantProbeStartupFilter.NoTenant, ResolvedTenant(response));
    }

    [Fact]
    public async Task AnAnonymousGymRouteIsChallenged_NotHidden()
    {
        // Grants spec D2. 404 is the right answer for "you hold nothing on this gym"; it is the wrong
        // answer for "you have not said who you are" — that is authentication's 401, and it must carry
        // the bearer challenge every other anonymous request gets. Today this holds only because
        // authorization runs first and the middleware never sees an anonymous request; once the
        // middleware moves in front of authorization (H1), it has to let anonymous callers through
        // itself, or every anonymous gym route becomes a 404.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "anonymous-route-gym");
        using HttpClient anonymous = _host.CreateClient();

        using HttpResponseMessage response = await anonymous.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, challenge =>
            string.Equals(challenge.Scheme, "Bearer", StringComparison.Ordinal));

        // And nothing was resolved for nobody.
        Assert.Equal(ResolvedTenantProbeStartupFilter.NoTenant, ResolvedTenant(response));
    }

    [Fact]
    public async Task AGymScopedGrantHoldsNothingOnAPlatformRoute()
    {
        // Grants spec F1 — the escalation itself. SupportAgent holds audit.read. A SupportAgent grant
        // scoped to one gym must not make the platform-wide audit log readable; before H1 the
        // permission join filtered on UserId alone and it did.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-grant-gym");
        using HttpClient scoped = await SignedInWithGrantForAsync(gym);

        using HttpResponseMessage response = await scoped.GetAsync(
            new Uri("/api/v1/audit-logs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AGymScopedGrantHoldsItsPermissionsOnThatGymsRoutes()
    {
        // The negative control for the test above (grants spec §4, test 2): an evaluator that returned
        // nothing at all for a gym-scoped caller would pass it and be useless. SupportAgent holds
        // gyms.read; on the gym the grant names, that is a 200 — and the tenant was set for it.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "scoped-grant-home-gym");
        using HttpClient scoped = await SignedInWithGrantForAsync(gym);

        using HttpResponseMessage response = await scoped.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(gym.ToString(), ResolvedTenant(response));
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
    /// What <c>ITenantContext.TenantId</c> held at the end of the request that produced
    /// <paramref name="response"/>, as reported by <see cref="ResolvedTenantProbeStartupFilter"/>.
    /// </summary>
    private static string ResolvedTenant(HttpResponseMessage response)
    {
        Assert.True(
            response.Headers.TryGetValues(ResolvedTenantProbeStartupFilter.HeaderName, out var values),
            $"The {ResolvedTenantProbeStartupFilter.HeaderName} header is missing, so the probe did not " +
            "run — the assertion below would pass or fail for the wrong reason.");

        return Assert.Single(values!);
    }

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

/// <summary>
/// Reports what <see cref="ITenantContext.TenantId"/> held during a request, on a response header, so a
/// test can assert the chain <c>route → TenantResolutionMiddleware → ITenantContext</c> end to end
/// rather than inferring it from a 200.
/// </summary>
/// <remarks>
/// <para><b>Test-host only.</b> Registered through <c>AuthenticationHost.Create</c>'s
/// <c>configureServices</c> hook by <see cref="TenantResolutionTests"/> alone; nothing is added to
/// production routing or to the production pipeline, and no other suite's host sees it.</para>
/// <para><b>A middleware rather than a test-only endpoint</b>, deliberately. An endpoint that returns
/// the tenant would prove the chain works on a route invented for the test; this reports it for
/// whichever <em>real</em> route the test called, so <c>/api/v1/gyms/{gymId}</c> — the actual shape the
/// Members increment inherits — is what gets asserted on.</para>
/// <para><b>Read on <c>OnStarting</c>, not before <c>next</c>.</b> The value is captured as the response
/// begins, which is after <c>TenantResolutionMiddleware</c> and the endpoint have run, and while the
/// request scope is still alive. Reading it before <c>next</c> would always report null; reading it
/// after <c>next</c> returns is too late to add a header.</para>
/// <para>An <see cref="IStartupFilter"/> because <c>WebApplicationFactory</c> offers no seam to insert
/// middleware into the middle of a minimal-hosting pipeline. Filters registered here run inside the
/// host's own <c>RequestServicesContainerMiddleware</c> (registered first, and so applied outermost),
/// which is what makes <c>HttpContext.RequestServices</c> available.</para>
/// </remarks>
internal sealed class ResolvedTenantProbeStartupFilter : IStartupFilter
{
    public const string HeaderName = "X-Test-Resolved-Tenant";

    /// <summary>
    /// Platform scope, as a header value. A literal rather than an empty string: assigning empty
    /// <c>StringValues</c> removes the header, and an absent header would be indistinguishable from a
    /// probe that never ran.
    /// </summary>
    public const string NoTenant = "none";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);

        return app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                context.Response.OnStarting(() =>
                {
                    var tenant = context.RequestServices.GetRequiredService<ITenantContext>();
                    context.Response.Headers[HeaderName] = tenant.TenantId?.ToString() ?? NoTenant;
                    return Task.CompletedTask;
                });

                await nextMiddleware();
            });

            next(app);
        };
    }
}
