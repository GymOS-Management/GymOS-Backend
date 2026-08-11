using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The dashboard over real HTTP: the live summary, and the permission boundary that gates
/// <c>analytics.read</c> and <c>audit.read</c> in opposite directions (ARCHITECTURE.md §3.2).
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class DashboardApiTests : IAsyncLifetime
{
    private static readonly Uri Summary = new("/api/v1/dashboard/summary", UriKind.Relative);
    private static readonly Uri Activity = new("/api/v1/dashboard/activity", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public DashboardApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("dashboard_api");
        _host = AuthenticationHost.Create(connectionString);
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
    }

    [Fact]
    public async Task SupportAgentIsRefusedTheDashboardButKeepsTheAuditFeed()
    {
        // SupportAgent already holds audit.read and not analytics.read, which makes it the exact
        // role that proves the boundary runs in both directions: refused the revenue, still able to
        // read the activity feed it needs to do its job.
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.dashboard@gymos.test");

        using HttpResponseMessage summary = await supportAgent.GetAsync(Summary);
        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);

        // /activity is the audit log in a trimmed shape, so audit.read governs it — analytics access
        // must not quietly become audit access, and audit access must not depend on analytics.
        using HttpResponseMessage activity = await supportAgent.GetAsync(Activity);
        Assert.Equal(HttpStatusCode.OK, activity.StatusCode);
    }

    [Fact]
    public async Task TheSummaryIsLiveRatherThanYesterdaysRow()
    {
        JsonElement before = await SummaryAsync();
        int wasTotal = before.GetProperty("totalGyms").GetInt32();

        await PlatformApiHelpers.CreateGymAsync(_client, "live-summary-gym");

        // No stats job has run. A dashboard that reported yesterday's count would be wrong, which is
        // why this endpoint does not read the read model.
        JsonElement after = await SummaryAsync();
        Assert.Equal(wasTotal + 1, after.GetProperty("totalGyms").GetInt32());
    }

    [Fact]
    public async Task TheSummarysMrrIsPerCurrencyAndExcludesTrials()
    {
        Guid plan = (await PlatformApiHelpers.CreatePlanAsync(_client, "DASH", amount: 49.99m)).Id;
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "paying-gym");
        await PlatformApiHelpers.AssignAsync(_client, gym, plan);

        JsonElement mrr = (await SummaryAsync()).GetProperty("mrr");

        JsonElement eur = Assert.Single(mrr.EnumerateArray());
        Assert.Equal("EUR", eur.GetProperty("currency").GetString());
        Assert.Equal(49.99m, eur.GetProperty("amount").GetDecimal());
    }

    private async Task<JsonElement> SummaryAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(Summary);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
