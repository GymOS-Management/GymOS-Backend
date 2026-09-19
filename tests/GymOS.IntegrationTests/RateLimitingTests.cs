using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The global rate limiter over real HTTP (M7 spec §7): per-user throttling for an authenticated
/// caller, health left untouched, and a raised limit taking effect on a new partition without a
/// restart. The sign-in policy's own IP-scoped limit (unchanged by this task) gets one more test
/// here to prove the global limiter did not swallow it. Kept in its own host, with
/// <c>ApiRequestsPerMinute</c> set low, so this suite's low budget does not throttle the others.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class RateLimitingTests : IAsyncLifetime
{
    private const int PermitLimit = 3;

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public RateLimitingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("rate_limiting");

        _host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Security:RateLimiting:ApiRequestsPerMinute"] = PermitLimit.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            settings["Jobs:Enabled"] = "false";
        });
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task TheFourthAuthenticatedCallInAMinuteIs429WithRetryAfter()
    {
        // The SuperAdmin session's "user:" partition is untouched until now (InitializeAsync only
        // ever called an unauthenticated route), so these are its first three requests.
        for (int attempt = 0; attempt < PermitLimit; attempt++)
        {
            using HttpResponseMessage allowed = await _client.GetAsync(
                new Uri("/api/v1/monitoring/jobs", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using HttpResponseMessage throttled = await _client.GetAsync(
            new Uri("/api/v1/monitoring/jobs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);

        using JsonDocument body = JsonDocument.Parse(await throttled.Content.ReadAsStringAsync());
        Assert.Equal("security.rate_limited", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task HealthIsNeverLimited()
    {
        // A fresh, unauthenticated client: proves health is exempt regardless of which partition
        // (user or IP) would otherwise apply, not merely that this test's SuperAdmin never
        // exhausts a per-route allowance.
        using HttpClient anonymous = _host.CreateClient();

        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage response = await anonymous.GetAsync(
                new Uri("/health/ready", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ASavedSettingRaisesTheLimitWithoutARestart()
    {
        // Provisioning a role-narrowed user costs several authenticated admin calls (find the
        // seeded user, look up the role, read then replace the role assignment) — more than this
        // fixture's three-per-minute budget allows for one session. Doing that bookkeeping through
        // a second, unthrottled host against the same database — a fresh SuperAdmin session, not a
        // fresh partition — leaves _client's own three calls for the one request that matters here.
        await using AuthenticationHost adminHost =
            AuthenticationHost.Create(_host.ConnectionString, settings => settings["Jobs:Enabled"] = "false");
        using HttpClient adminClient = adminHost.CreateClient();

        using HttpResponseMessage adminReady = await adminClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, adminReady.StatusCode);

        using HttpResponseMessage adminSignIn = await AuthApi.LoginAsync(
            adminClient, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.OK, adminSignIn.StatusCode);
        AuthApi.Authorize(adminClient, await AuthApi.ReadAccessTokenAsync(adminSignIn));

        // _client's first authenticated call on the throttled host — well inside its budget of
        // three — raises the limit that a brand-new partition will read.
        using HttpResponseMessage put = await _client.PutAsJsonAsync(
            new Uri("/api/v1/settings", UriKind.Relative),
            new
            {
                category = "Security",
                values = new[] { new { key = "security.api_requests_per_minute", value = "100" } },
            });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // A fresh user, signed in against the throttled host itself: its "user:" partition does
        // not exist yet, so its first request reads today's saved value, not the one this host
        // booted with.
        using HttpClient readOnly = await PlatformApiHelpers.SignedInAsync(
            _host, adminClient, "ReadOnly", "read.only.rate.limit@gymos.test");

        for (int i = 0; i < 10; i++)
        {
            using HttpResponseMessage response = await readOnly.GetAsync(
                new Uri("/api/v1/settings?category=General", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task SignInIsStillLimitedPerAddress()
    {
        // The sign-in policy's own default (Security:RateLimiting:SignInAttemptsPerWindow, 10) is
        // untouched by this fixture — only ApiRequestsPerMinute is overridden — so the global
        // limiter's low budget must not have swallowed or loosened it. A dedicated host, not
        // _host: InitializeAsync's own SuperAdmin sign-in already spent one of the ten permits on
        // _host's address bucket (TestServer gives every client of one host the same simulated
        // remote address), and this test needs a virgin one.
        await using AuthenticationHost signInHost =
            AuthenticationHost.Create(_host.ConnectionString, settings => settings["Jobs:Enabled"] = "false");
        using HttpClient anonymous = signInHost.CreateClient();

        for (int attempt = 0; attempt < 10; attempt++)
        {
            using HttpResponseMessage response = await AuthApi.LoginAsync(
                anonymous, AuthenticationHost.SuperAdminEmail, "wrong-password");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using HttpResponseMessage eleventh = await AuthApi.LoginAsync(
            anonymous, AuthenticationHost.SuperAdminEmail, "wrong-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
        Assert.NotNull(eleventh.Headers.RetryAfter);
    }
}
