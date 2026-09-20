using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Maintenance mode over real HTTP (M7 spec §6): the confirmed toggle, and the middleware that closes
/// the platform to everyone but a SuperAdmin while it is on — health, sign-in, and a `settings.manage`
/// holder's own requests keep working; everything else gets 503 naming the message, with `Retry-After`.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MaintenanceModeTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public MaintenanceModeTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("maintenance_mode");
        _host = AuthenticationHost.Create(connectionString, settings => settings["Jobs:Enabled"] = "false");
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
    public async Task TogglingWithoutTheHeaderIs400()
    {
        using HttpResponseMessage response =
            await ToggleMaintenanceAsync(enabled: true, "down for maintenance", confirmation: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("settings.confirmation_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task TogglingWithTheWrongHeaderIs400Mismatch()
    {
        using HttpResponseMessage response =
            await ToggleMaintenanceAsync(enabled: true, "down for maintenance", confirmation: "nope");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("settings.confirmation_mismatch", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhenOnAnonymousAndOtherRolesGet503WithRetryAfter()
    {
        const string Message = "GymOS is closed for scheduled maintenance.";
        using HttpResponseMessage toggle = await ToggleMaintenanceAsync(enabled: true, Message, "maintenance");
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);

        using HttpClient anonymous = _host.CreateClient();
        using HttpResponseMessage anonymousResponse = await anonymous.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, anonymousResponse.StatusCode);
        Assert.Equal("300", Assert.Single(anonymousResponse.Headers.GetValues("Retry-After")));
        using JsonDocument anonymousBody = JsonDocument.Parse(await anonymousResponse.Content.ReadAsStringAsync());
        Assert.Equal(Message, anonymousBody.RootElement.GetProperty("detail").GetString());
        Assert.Equal("settings.maintenance", anonymousBody.RootElement.GetProperty("code").GetString());

        using HttpClient readOnly = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "ReadOnly", "read.only.maintenance@gymos.test");
        using HttpResponseMessage readOnlyResponse = await readOnly.GetAsync(
            new Uri("/api/v1/settings", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readOnlyResponse.StatusCode);
    }

    [Fact]
    public async Task WhenOnSuperAdminHealthAndSignInStillPass()
    {
        const string ReadOnlyEmail = "read.only.maintenance.signin@gymos.test";

        // PlatformApiHelpers.SignedInAsync's fixed password for a role-narrowed test session.
        const string ReadOnlyPassword = "role-holder-integration-password";

        // Provisioned through the SuperAdmin session before asserting anything about maintenance —
        // the SuperAdmin's own requests pass regardless (settings.manage), so order does not matter
        // for this call, but the user must exist before the sign-in assertion below.
        (await PlatformApiHelpers.SignedInAsync(_host, _client, "ReadOnly", ReadOnlyEmail)).Dispose();

        using HttpResponseMessage toggle = await ToggleMaintenanceAsync(enabled: true, "maintenance window", "maintenance");
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);

        using HttpResponseMessage superAdminSettings = await _client.GetAsync(
            new Uri("/api/v1/settings", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, superAdminSettings.StatusCode);

        using HttpClient anonymous = _host.CreateClient();

        using HttpResponseMessage ready = await anonymous.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage version = await anonymous.GetAsync(new Uri("/version", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);

        using HttpResponseMessage login = await AuthApi.LoginAsync(anonymous, ReadOnlyEmail, ReadOnlyPassword);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task WhenOnSuperAdminPassesOnAGymRouteToo()
    {
        // H1 moved TenantResolutionMiddleware in front of this one. The SuperAdmin's pass is a
        // permission check that now runs with the tenant already set; it must still say yes.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "maintenance-gym");

        using HttpResponseMessage toggle = await ToggleMaintenanceAsync(enabled: true, "maintenance window", "maintenance");
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);

        using HttpResponseMessage response = await _client.GetAsync(new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TurningItOffRestoresService()
    {
        // SupportAgent holds monitoring.read but not settings.manage (MonitoringApiTests.
        // SupportAgentMayReadMonitoring), so this session is genuinely refused while maintenance is
        // on and genuinely restored once it is off — unlike an anonymous caller, whose 401 for
        // missing credentials would look the same before and after.
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.agent.maintenance@gymos.test");

        using HttpResponseMessage on = await ToggleMaintenanceAsync(enabled: true, "brb", "maintenance");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        using HttpResponseMessage closed = await supportAgent.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, closed.StatusCode);

        using HttpResponseMessage off = await ToggleMaintenanceAsync(enabled: false, message: null, "maintenance");
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);

        using HttpResponseMessage restored = await supportAgent.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
    }

    [Fact]
    public async Task WhenOnARefusedCallerCanStillSignOut()
    {
        // SupportAgent holds monitoring.read but not settings.manage, so this session is
        // genuinely refused while maintenance is on (TurningItOffRestoresService) — exactly
        // the caller who would otherwise be trapped: the console's logout call refused, the
        // refresh cookie surviving, every reload bootstrapping the same refused session again.
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.agent.maintenance.logout@gymos.test");

        using HttpResponseMessage on = await ToggleMaintenanceAsync(enabled: true, "brb", "maintenance");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);

        using HttpResponseMessage signOut = await supportAgent.PostAsync(AuthApi.Logout, content: null);
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        using HttpResponseMessage stillClosed = await supportAgent.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, stillClosed.StatusCode);
    }

    [Fact]
    public async Task TheToggleIsAudited()
    {
        using HttpResponseMessage toggle = await ToggleMaintenanceAsync(enabled: true, "audited change", "maintenance");
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);

        using HttpResponseMessage list = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?action=settings.maintenance_mode_changed", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement items = listBody.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/audit-logs/{items[0].GetProperty("id").GetGuid()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using JsonDocument detailBody = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        using JsonDocument newValues = JsonDocument.Parse(detailBody.RootElement.GetProperty("newValues").GetString()!);
        Assert.Equal("True", newValues.RootElement.GetProperty("enabled").GetString());
    }

    [Fact]
    public async Task PutSettingsCannotToggleIt()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri("/api/v1/settings", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                category = "Maintenance",
                values = new[] { new { key = "maintenance.enabled", value = "true" } },
            }),
        };
        request.Headers.TryAddWithoutValidation("X-Confirm-Action", "maintenance");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("settings.use_maintenance_endpoint", body.RootElement.GetProperty("code").GetString());
    }

    private async Task<HttpResponseMessage> ToggleMaintenanceAsync(bool enabled, string? message, string? confirmation)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/v1/settings/maintenance-mode", UriKind.Relative))
        {
            Content = JsonContent.Create(new { enabled, message }),
        };

        if (confirmation is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Confirm-Action", confirmation);
        }

        return await _client.SendAsync(request);
    }
}
