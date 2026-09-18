using System.Net;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The IP-scoped half of sign-in throttling (the per-account half is the domain lockout,
/// covered by the Application unit tests). Kept in its own host so a low permit limit
/// does not throttle the other suites.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SignInRateLimitingTests : IAsyncLifetime
{
    private const int PermitLimit = 3;

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public SignInRateLimitingTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("sign_in_rate_limit_tests");

        _host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Security:RateLimiting:SignInAttemptsPerWindow"] =
                PermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["Security:RateLimiting:SignInWindow"] = "00:05:00";

            // A high lockout threshold keeps this test about the rate limiter: without it
            // the account would lock first and the 401s would stop meaning what they mean.
            settings["Security:LoginPolicy:MaxFailedAttempts"] = "100";
        });

        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    [Fact]
    public async Task AttemptsBeyondThePermitLimit_AreRejectedWith429AndRetryAfter()
    {
        for (int attempt = 0; attempt < PermitLimit; attempt++)
        {
            using HttpResponseMessage allowed = await AuthApi.LoginAsync(
                _client, AuthenticationHost.SuperAdminEmail, "wrong-password");

            // Within the limit, a bad password is a plain 401 — the limiter is not involved.
            Assert.Equal(HttpStatusCode.Unauthorized, allowed.StatusCode);
        }

        using HttpResponseMessage throttled = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, "wrong-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.NotNull(throttled.Headers.RetryAfter);
    }

    [Fact]
    public async Task TheLimiterGuardsSignInOnly_NotTheWholeApi()
    {
        for (int attempt = 0; attempt <= PermitLimit; attempt++)
        {
            using HttpResponseMessage _ = await AuthApi.LoginAsync(
                _client, AuthenticationHost.SuperAdminEmail, "wrong-password");
        }

        // An exhausted sign-in bucket must not take the rest of the API down with it —
        // otherwise a single attacker could deny service to every signed-in user.
        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        using HttpResponseMessage refresh = await _client.PostAsync(AuthApi.Refresh, content: null);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }
}
