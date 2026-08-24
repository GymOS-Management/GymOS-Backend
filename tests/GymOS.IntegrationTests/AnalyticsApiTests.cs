using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Analytics;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.SharedKernel.Currencies;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The three recorded series over real HTTP: the range rule, the permission boundary, and the
/// design rule the whole surface exists to prove — a missing day comes back as a gap, never a
/// zero (ARCHITECTURE.md §3.8). Rows are seeded directly through the DbContext
/// (<c>DailyPlatformStats.Record</c>): these endpoints read the read model, so seeding it directly
/// is the honest unit of test here, not a round trip through the nightly job.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class AnalyticsApiTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public AnalyticsApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("analytics_api");
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
    public async Task AGapInTheSeriesIsReturnedAsAGapAndNeverAsZeros()
    {
        await SeedAsync(new DateOnly(2026, 8, 1), total: 10);
        await SeedAsync(new DateOnly(2026, 8, 3), total: 12); // the 2nd is missing on purpose

        JsonElement series = await GetAsync("/api/v1/analytics/gyms-growth?from=2026-08-01&to=2026-08-03");

        Assert.Equal(2, series.GetArrayLength());
        Assert.Equal("2026-08-01", series[0].GetProperty("date").GetString());
        Assert.Equal("2026-08-03", series[1].GetProperty("date").GetString());
    }

    [Fact]
    public async Task ChurnReportsItsTwoCausesSeparately()
    {
        await SeedChurnAsync(new DateOnly(2026, 8, 1), canceled: 2, expired: 3);

        JsonElement point = (await GetAsync("/api/v1/analytics/churn?from=2026-08-01&to=2026-08-01"))[0];

        Assert.Equal(2, point.GetProperty("canceled").GetInt32());
        Assert.Equal(3, point.GetProperty("expired").GetInt32());
        Assert.Equal(5, point.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RevenueCarriesEachCurrencySeparately()
    {
        await SeedRevenueAsync(new DateOnly(2026, 8, 1), [(120m, "EUR"), (80m, "GBP")]);

        JsonElement mrr = (await GetAsync("/api/v1/analytics/revenue?from=2026-08-01&to=2026-08-01"))[0]
            .GetProperty("mrr");

        Assert.Equal(2, mrr.GetArrayLength());
    }

    [Fact]
    public async Task AnOverlongRangeIsRefusedRatherThanServedSlowly()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/analytics/revenue?from=2025-01-01&to=2026-12-31", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("analytics.range_invalid", await AuthApi.ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task SupportAgentCannotReadTheSeries()
    {
        // SupportAgent holds audit.read, not analytics.read (Task 4) — the same boundary the
        // dashboard enforces, proved again here for the series endpoints.
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.analytics@gymos.test");

        using HttpResponseMessage response = await supportAgent.GetAsync(
            new Uri("/api/v1/analytics/churn?from=2026-08-01&to=2026-08-01", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<JsonElement> GetAsync(string relativeUrl)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(relativeUrl, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedAsync(DateOnly date, int total)
    {
        await using AppDbContext context = _host.CreateDbContext();
        context.Add(DailyPlatformStats.Record(
            date, new GymCounts(total, total, 0, 0, 0, 0, 0), [], DateTimeOffset.UnixEpoch));
        await context.SaveChangesAsync();
    }

    private async Task SeedChurnAsync(DateOnly date, int canceled, int expired)
    {
        await using AppDbContext context = _host.CreateDbContext();
        context.Add(DailyPlatformStats.Record(
            date, new GymCounts(0, 0, 0, 0, 0, canceled, expired), [], DateTimeOffset.UnixEpoch));
        await context.SaveChangesAsync();
    }

    private async Task SeedRevenueAsync(DateOnly date, (decimal Amount, string Currency)[] mrr)
    {
        await using AppDbContext context = _host.CreateDbContext();
        context.Add(DailyPlatformStats.Record(
            date,
            new GymCounts(0, 0, 0, 0, 0, 0, 0),
            mrr.Select(entry => Money.Of(entry.Amount, Currency.Of(entry.Currency))).ToList(),
            DateTimeOffset.UnixEpoch));
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task RevenueOrdersEachDaysCurrenciesDeterministically()
    {
        // The collection join carries no ORDER BY of its own, so two currencies on one day were a tie
        // the database could break either way — insertion order today, something else after a hash
        // join or a table rewrite. MonthlyRecurringRevenue.Calculate sorts ordinally by code and
        // /dashboard/summary passes that through, so an unordered /analytics/revenue could report the
        // same platform's currencies in the opposite order, and a client keyed on array position
        // would swap its two series with nothing to explain why.
        //
        // Seeded deliberately out of ordinal order (GBP before EUR) so the assertion cannot pass by
        // accident of insertion.
        var date = new DateOnly(2026, 5, 4);
        await SeedRevenueAsync(date, [(80m, "GBP"), (120m, "EUR")]);

        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/analytics/revenue?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        JsonElement mrr = body.EnumerateArray().Single().GetProperty("mrr");

        Assert.Collection(
            mrr.EnumerateArray(),
            first => Assert.Equal("EUR", first.GetProperty("currency").GetString()),
            second => Assert.Equal("GBP", second.GetProperty("currency").GetString()));
    }

}
