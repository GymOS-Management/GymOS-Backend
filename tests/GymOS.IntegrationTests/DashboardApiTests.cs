using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The dashboard over real HTTP: the live summary, and the permission boundary that gates
/// <c>analytics.read</c> and <c>audit.read</c> in opposite directions (ARCHITECTURE.md §3.2).
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class DashboardApiTests : IAsyncLifetime
{
    private static readonly Uri Summary = new("/api/v1/dashboard/summary", UriKind.Relative);
    private static readonly Uri Health = new("/api/v1/dashboard/health", UriKind.Relative);
    private static readonly Uri Activity = new("/api/v1/dashboard/activity", UriKind.Relative);

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public DashboardApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("dashboard_api");
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
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
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

    /// <summary>
    /// The control <c>SupportAgentIsRefusedTheDashboardButKeepsTheAuditFeed</c> lacked: deleting the
    /// <c>analytics.read</c> grant from any of these roles left every M5 test green, because nothing
    /// asserted a role that holds it gets through. SuperAdmin is not a control — it grants everything
    /// and proves nothing about the row.
    /// </summary>
    [Theory]
    [InlineData("PlatformAdmin")]
    [InlineData("BillingAdmin")]
    [InlineData("ReadOnly")]
    public async Task ARoleHoldingAnalyticsReadCanReadTheSummary(string role)
    {
        using HttpClient holder = await PlatformApiHelpers.SignedInAsync(
            _host, _client, role, $"{role.ToLowerInvariant()}.summary@gymos.test");

        using HttpResponseMessage response = await holder.GetAsync(Summary);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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

    [Fact]
    public async Task TheSummarysMrrIsOrderedByCurrencyCode()
    {
        // The order is Calculate's ordinal-by-code contract, shared with /analytics/revenue, asserted
        // here at the API boundary.
        PlatformApiHelpers.PlanCreated plan = await PlatformApiHelpers.CreatePlanAsync(_client, "DASHTWO", amount: 20m);
        await AddPriceAsync(plan, "GBP", 30m);
        Guid gbpGym = await PlatformApiHelpers.CreateGymAsync(_client, "gbp-gym");
        Guid eurGym = await PlatformApiHelpers.CreateGymAsync(_client, "eur-gym");
        await PlatformApiHelpers.AssignAsync(_client, gbpGym, plan.Id, currency: "GBP");
        await PlatformApiHelpers.AssignAsync(_client, eurGym, plan.Id);

        JsonElement mrr = (await SummaryAsync()).GetProperty("mrr");

        Assert.Equal(
            ["EUR", "GBP"],
            mrr.EnumerateArray().Select(entry => entry.GetProperty("currency").GetString()).ToList());
    }

    private async Task AddPriceAsync(PlatformApiHelpers.PlanCreated plan, string currency, decimal amount)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{plan.Id}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency, amount }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{plan.RowVersion}\"");

        using HttpResponseMessage priced = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);
    }

    [Fact]
    public async Task HealthReportsChecksWithoutLeakingExceptionDetails()
    {
        // This also proves out IDashboardHealthCheck's registration: /health resolves it from the
        // container via DashboardHealthCheckAdapter, the one Application abstraction Program.cs
        // wires by hand rather than through AddPlatformAnalytics.
        using HttpResponseMessage response = await _client.GetAsync(Health);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement checks = body.GetProperty("checks");
        Assert.NotEmpty(checks.EnumerateArray());

        // The stated security property (DashboardHealthCheckAdapter's own comment): no Exception, no
        // Description are projected anywhere in the payload, because either can carry a connection
        // string or a stack trace and this endpoint feeds a console panel, not a debugger. Asserted
        // structurally, at any depth, rather than trusted from reading the projection code.
        AssertNoSensitiveKeys(body);
    }

    private static void AssertNoSensitiveKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    Assert.False(
                        string.Equals(property.Name, "description", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(property.Name, "exception", StringComparison.OrdinalIgnoreCase),
                        $"Found forbidden key '{property.Name}' in the dashboard health response.");
                    AssertNoSensitiveKeys(property.Value);
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    AssertNoSensitiveKeys(item);
                }

                break;
        }
    }

    private async Task<JsonElement> SummaryAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(Summary);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
