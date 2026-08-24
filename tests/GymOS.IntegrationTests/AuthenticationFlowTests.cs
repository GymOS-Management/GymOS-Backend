using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The M1.2 acceptance path end to end over real HTTP against real SQL Server:
/// sign in → refresh → reuse detection → sign out, plus <c>/me</c> and change-password.
/// Everything below goes through the wire, so cookie attributes, status codes, problem
/// shapes, and token validation are all under test — not just the handlers.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AuthenticationFlowTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public AuthenticationFlowTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("auth_flow");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        // Force the host to boot (migrations + catalog sync + SuperAdmin seed) before
        // the first assertion, so a startup failure fails loudly rather than as a 404.
        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task SignIn_ReturnsAnAccessTokenAndSetsTheRefreshCookie()
    {
        using HttpResponseMessage response = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("accessToken").GetString()));
        Assert.Equal("Bearer", body.RootElement.GetProperty("tokenType").GetString());
        Assert.True(body.RootElement.GetProperty("expiresInSeconds").GetInt32() > 0);

        // The refresh token must never appear in the body — cookie only.
        Assert.DoesNotContain("refreshToken", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        string setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("gymos_refresh_token=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/auth", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheSeededSuperAdmin_SignsIn_AndHoldsEveryPlatformPermission()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));

        using HttpResponseMessage me = await _client.GetAsync(AuthApi.Me);

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());

        Assert.Equal(AuthenticationHost.SuperAdminEmail, body.RootElement.GetProperty("email").GetString());
        Assert.Equal(nameof(UserStatus.Active), body.RootElement.GetProperty("status").GetString());
        Assert.Contains(
            SystemRoles.SuperAdmin,
            body.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));

        var permissions = body.RootElement.GetProperty("permissions")
            .EnumerateArray().Select(permission => permission.GetString()).ToList();
        Assert.Equal(PlatformPermissions.All.Count, permissions.Count);
        Assert.Contains(PlatformPermissions.UsersManage, permissions);
    }

    [Fact]
    public async Task Refresh_RotatesTheCookie_AndReplayingTheOldOneIsRefused()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        string firstCookie = ExtractRefreshCookie(signIn);

        using HttpResponseMessage refreshed = await PostWithRefreshCookie(AuthApi.Refresh, firstCookie);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        string rotatedCookie = ExtractRefreshCookie(refreshed);
        Assert.NotEqual(firstCookie, rotatedCookie);

        // The rotated token works...
        using HttpResponseMessage withRotated = await PostWithRefreshCookie(AuthApi.Refresh, rotatedCookie);
        Assert.Equal(HttpStatusCode.OK, withRotated.StatusCode);

        // ...and the replayed original does not. This is the leak signal.
        using HttpResponseMessage replay = await PostWithRefreshCookie(AuthApi.Refresh, firstCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("auth.invalid_refresh_token", await AuthApi.ReadProblemCodeAsync(replay));
    }

    [Fact]
    public async Task ReplayingALeakedToken_KillsTheSessionThatDescendedFromIt()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        string leaked = ExtractRefreshCookie(signIn);

        using HttpResponseMessage rotated = await PostWithRefreshCookie(AuthApi.Refresh, leaked);
        string liveSession = ExtractRefreshCookie(rotated);

        // The leaked token is replayed — by the attacker, or by the victim after the
        // attacker used it. Either way the chain is compromised.
        using HttpResponseMessage replay = await PostWithRefreshCookie(AuthApi.Refresh, leaked);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // The session that descended from the leak is now dead too: whoever holds it
        // must authenticate again, which the attacker cannot do.
        using HttpResponseMessage afterRevocation = await PostWithRefreshCookie(AuthApi.Refresh, liveSession);
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);

        await using AsyncServiceScope scope = _host.Services.CreateAsyncScope();
        AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Contains(
            await dbContext.Set<RefreshToken>().Select(token => token.RevocationReason).ToListAsync(),
            reason => reason == "reuse_detected");
    }

    [Fact]
    public async Task SignOut_RevokesTheSession_AndClearsTheCookie()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        string cookie = ExtractRefreshCookie(signIn);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));

        using HttpResponseMessage signOut = await PostWithRefreshCookie(AuthApi.Logout, cookie);

        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.Contains(
            signOut.Headers.GetValues("Set-Cookie"),
            header => header.Contains("gymos_refresh_token=", StringComparison.Ordinal));

        using HttpResponseMessage afterSignOut = await PostWithRefreshCookie(AuthApi.Refresh, cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, afterSignOut.StatusCode);
    }

    [Fact]
    public async Task WrongPassword_Returns401ProblemJson_WithTheStableCode()
    {
        using HttpResponseMessage response = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("auth.invalid_credentials", await AuthApi.ReadProblemCodeAsync(response));
        Assert.DoesNotContain(response.Headers, header => header.Key == "Set-Cookie");
    }

    [Fact]
    public async Task UnknownEmail_IsIndistinguishableFromAWrongPassword()
    {
        using HttpResponseMessage response = await AuthApi.LoginAsync(
            _client, "no-such-admin@gymos.test", "whatever-it-is");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_credentials", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task ProtectedEndpoints_RefuseUnauthenticatedCallers()
    {
        using HttpResponseMessage me = await _client.GetAsync(AuthApi.Me);
        using HttpResponseMessage changePassword = await AuthApi.ChangePasswordAsync(_client, "a", "b");

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, changePassword.StatusCode);
    }

    [Fact]
    public async Task AForgedAccessToken_IsRejected()
    {
        // Signed with a different key: the signature check is what stands between a
        // crafted `sub` claim and full platform access.
        AuthApi.Authorize(
            _client,
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiIwMDAwMDAwMC0wMDAwLTAwMDAtMDAwMC0wMDAwMDAwMDAwMDEiLCJleHAiOjQwNzA5MDg4MDB9." +
            "not-a-valid-signature");

        using HttpResponseMessage me = await _client.GetAsync(AuthApi.Me);

        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task RefreshWithNoCookie_Returns401()
    {
        using HttpResponseMessage response = await _client.PostAsync(AuthApi.Refresh, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_refresh_token", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task ChangePassword_SwapsTheCredential_AndEndsEverySession()
    {
        const string NewPassword = "a-different-integration-password";

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        string cookieBefore = ExtractRefreshCookie(signIn);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));

        using HttpResponseMessage changed = await AuthApi.ChangePasswordAsync(
            _client, AuthenticationHost.SuperAdminPassword, NewPassword);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // The old session cannot be refreshed back into life.
        using HttpResponseMessage staleRefresh = await PostWithRefreshCookie(AuthApi.Refresh, cookieBefore);
        Assert.Equal(HttpStatusCode.Unauthorized, staleRefresh.StatusCode);

        _client.DefaultRequestHeaders.Authorization = null;

        using HttpResponseMessage oldPassword = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        using HttpResponseMessage newPassword = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, NewPassword);
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_RejectsANewPasswordBelowThePolicy()
    {
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));

        using HttpResponseMessage response = await AuthApi.ChangePasswordAsync(
            _client, AuthenticationHost.SuperAdminPassword, "short");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task TheAuthEndpointsAppearInTheVersionedOpenApiDocument()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        string document = await response.Content.ReadAsStringAsync();

        // R12: every public endpoint is discoverable in the versioned contract, which is
        // what the generated frontend clients are built from (AD-12).
        Assert.Contains("/api/v1/auth/login", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/refresh", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/logout", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/me", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/auth/change-password", document, StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> PostWithRefreshCookie(Uri endpoint, string cookieValue)
    {
        // The client manages no cookies (see AuthenticationHost), so this header is the
        // only refresh token the server sees — which is what makes the replay assertions
        // mean what they say.
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Add("Cookie", $"gymos_refresh_token={cookieValue}");
        return _client.SendAsync(request);
    }

    private static string ExtractRefreshCookie(HttpResponseMessage response)
    {
        string header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("gymos_refresh_token=", StringComparison.Ordinal));

        return header.Split(';')[0]["gymos_refresh_token=".Length..];
    }
}
