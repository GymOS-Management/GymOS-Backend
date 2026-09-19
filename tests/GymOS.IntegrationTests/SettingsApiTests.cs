using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The settings surface over real HTTP (M7 spec §2-4): the catalog with sources, the mask on a
/// sensitive value, the batch write per category, and the permission boundary
/// (<c>settings.read</c>: PlatformAdmin and ReadOnly; <c>settings.manage</c>: SuperAdmin only).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SettingsApiTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public SettingsApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("settings_api");
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
    public async Task TheCatalogIsServedWithSourcesAndTheMask()
    {
        JsonElement views = await GetAsync("/api/v1/settings?category=Email");

        Assert.Equal(10, views.GetArrayLength());

        JsonElement port = Find(views, "email.smtp_port");
        Assert.Equal("1025", port.GetProperty("value").GetString());
        Assert.Equal("Default", port.GetProperty("source").GetString());

        JsonElement password = Find(views, "email.smtp_password");
        Assert.Equal(JsonValueKind.Null, password.GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task APutRoundTripsPlainValuesAndMasksSensitiveOnes()
    {
        using HttpResponseMessage put = await PutAsync(
            "Email",
            ("email.smtp_host", "row.example.test"),
            ("email.smtp_password", "hunter2"),
            ("email.smtp_port", "2525"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        JsonElement views = await GetAsync("/api/v1/settings?category=Email");

        JsonElement host = Find(views, "email.smtp_host");
        Assert.Equal("row.example.test", host.GetProperty("value").GetString());
        Assert.Equal("Database", host.GetProperty("source").GetString());

        JsonElement password = Find(views, "email.smtp_password");
        Assert.Equal(PlatformSettingDefinitions.Mask, password.GetProperty("value").GetString());
        Assert.Equal("Database", password.GetProperty("source").GetString());

        JsonElement port = Find(views, "email.smtp_port");
        Assert.Equal("2525", port.GetProperty("value").GetString());
        Assert.Equal("Database", port.GetProperty("source").GetString());
    }

    [Fact]
    public async Task TheMaskOnPutLeavesTheSecretUnchanged()
    {
        using HttpResponseMessage seed = await PutAsync("Email", ("email.smtp_password", "hunter2"));
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        string? before = await ReadProtectedValueAsync("email.smtp_password");
        Assert.NotNull(before);

        using HttpResponseMessage masked = await PutAsync(
            "Email", ("email.smtp_password", PlatformSettingDefinitions.Mask));
        Assert.Equal(HttpStatusCode.OK, masked.StatusCode);

        string? after = await ReadProtectedValueAsync("email.smtp_password");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task AnEmptyValueReturnsTheKeyToTheLayerBelow()
    {
        using HttpResponseMessage seed = await PutAsync("Email", ("email.smtp_port", "2525"));
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);

        using HttpResponseMessage cleared = await PutAsync("Email", ("email.smtp_port", ""));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        JsonElement views = await GetAsync("/api/v1/settings?category=Email");
        JsonElement port = Find(views, "email.smtp_port");
        Assert.Equal("1025", port.GetProperty("value").GetString());
        Assert.Equal("Default", port.GetProperty("source").GetString());
    }

    [Fact]
    public async Task AnInvalidStateIs400NamingTheRule()
    {
        using HttpResponseMessage response = await PutAsync(
            "Email", ("email.enabled", "true"), ("email.from_address", ""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Smtp:Host is required", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMaintenanceCategoryIs400Here()
    {
        using HttpResponseMessage response = await PutAsync("Maintenance", ("maintenance.enabled", "true"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("settings.use_maintenance_endpoint", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PlatformAdminMayReadButNotWrite()
    {
        using HttpClient platformAdmin = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "PlatformAdmin", "platform.admin.settings@gymos.test");

        using HttpResponseMessage read = await platformAdmin.GetAsync(
            new Uri("/api/v1/settings?category=Email", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using HttpResponseMessage write = await platformAdmin.PutAsJsonAsync(
            new Uri("/api/v1/settings", UriKind.Relative),
            new { category = "Email", values = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyMayReadAndSupportAgentMayNot()
    {
        using HttpClient readOnly = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "ReadOnly", "read.only.settings@gymos.test");
        using HttpResponseMessage readResponse = await readOnly.GetAsync(
            new Uri("/api/v1/settings?category=Email", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);

        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.agent.settings@gymos.test");
        using HttpResponseMessage forbidden = await supportAgent.GetAsync(
            new Uri("/api/v1/settings?category=Email", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task TheAuditLogRecordsThePlainValueAndRedactsTheSecret()
    {
        using HttpResponseMessage put = await PutAsync(
            "Email",
            ("email.smtp_host", "row.example.test"),
            ("email.smtp_password", "hunter2"),
            ("email.smtp_port", "2525"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        using HttpResponseMessage list = await _client.GetAsync(
            new Uri("/api/v1/audit-logs?entityType=platform_setting", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        JsonElement items = listBody.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() > 0);

        string? hostDetail = null;
        string? passwordDetail = null;
        foreach (JsonElement item in items.EnumerateArray())
        {
            using HttpResponseMessage detail = await _client.GetAsync(
                new Uri($"/api/v1/audit-logs/{item.GetProperty("id").GetGuid()}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            string body = await detail.Content.ReadAsStringAsync();

            if (body.Contains("email.smtp_host", StringComparison.Ordinal))
            {
                hostDetail = body;
            }

            if (body.Contains("email.smtp_password", StringComparison.Ordinal))
            {
                passwordDetail = body;
            }
        }

        Assert.NotNull(hostDetail);
        Assert.Contains("row.example.test", hostDetail, StringComparison.Ordinal);

        Assert.NotNull(passwordDetail);
        Assert.Contains("***REDACTED***", passwordDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", passwordDetail, StringComparison.Ordinal);
    }

    private static JsonElement Find(JsonElement views, string key) =>
        views.EnumerateArray().Single(view => view.GetProperty("key").GetString() == key);

    private async Task<JsonElement> GetAsync(string relativeUrl)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(relativeUrl, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> PutAsync(string category, params (string Key, string? Value)[] values) =>
        _client.PutAsJsonAsync(
            new Uri("/api/v1/settings", UriKind.Relative),
            new
            {
                category,
                values = values.Select(v => new { key = v.Key, value = v.Value }).ToArray(),
            });

    private async Task<string?> ReadProtectedValueAsync(string key)
    {
        await using AppDbContext context = _host.CreateDbContext();
        PlatformSetting row = await context.Set<PlatformSetting>().SingleAsync(s => s.Key == key);
        return row.ProtectedValue;
    }
}
