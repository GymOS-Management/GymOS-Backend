using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.BuildingBlocks.Infrastructure.Time;
using GymOS.Platform.Domain.Monitoring;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The monitoring surface over real HTTP (M6 spec §4–6): the empty platform is a valid platform, the
/// permission boundary is the opposite of analytics' (SupportAgent may read), history pages by time,
/// and an unknown job is a 404. Runs are seeded through the DbContext — the endpoints read rows, so
/// rows are the honest unit of test here, as <c>AnalyticsApiTests</c> seeds its read model.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class MonitoringApiTests : IAsyncLifetime
{
    private static readonly SystemClock Clock = new();

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public MonitoringApiTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("monitoring_api");
        // The host's timer stays off so the registered jobs do not write rows under the assertions.
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
    public async Task TheEmptyPlatformIsAValidOverview()
    {
        JsonElement overview = await GetAsync("/api/v1/monitoring/overview");

        Assert.Equal("Healthy", overview.GetProperty("health").GetProperty("status").GetString());
        JsonElement jobs = overview.GetProperty("jobs");
        Assert.True(jobs.GetArrayLength() >= 5); // subscription-renewal, invoice-overdue, retention, platform-stats, feature-override-sweep
        foreach (JsonElement job in jobs.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, job.GetProperty("lastRun").ValueKind);
            Assert.False(job.GetProperty("stale").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(job.GetProperty("schedule").GetString()));
        }

        // Not literally empty: seeding the SuperAdmin raises `UserCreated`, which the outbox
        // interceptor captures in the same transaction (AD-13) — so at least that one row exists
        // and `oldestMessageAgeSeconds` is a real, non-negative age rather than null.
        JsonElement outbox = overview.GetProperty("outbox");
        Assert.True(outbox.GetProperty("oldestMessageAgeSeconds").GetInt64() >= 0);
        Assert.Contains("processor", outbox.GetProperty("note").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ARecordedRunAppearsAsTheJobsLastRun()
    {
        await SeedAsync("subscription-renewal", Clock.UtcNow.AddMinutes(-2), JobRunStatus.Failed, "InvalidOperationException: boom");

        JsonElement jobs = await GetAsync("/api/v1/monitoring/jobs");
        JsonElement renewal = jobs.EnumerateArray().Single(job => job.GetProperty("name").GetString() == "subscription-renewal");

        Assert.Equal("Failed", renewal.GetProperty("lastRun").GetProperty("status").GetString());
        Assert.Equal("InvalidOperationException: boom", renewal.GetProperty("lastRun").GetProperty("error").GetString());
        Assert.True(renewal.GetProperty("stale").GetBoolean()); // ran, never succeeded
    }

    [Fact]
    public async Task SupportAgentMayReadMonitoring()
    {
        using HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.monitoring@gymos.test");

        using HttpResponseMessage response = await supportAgent.GetAsync(
            new Uri("/api/v1/monitoring/overview", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BillingAdminIsRefused()
    {
        using HttpClient billingAdmin = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "BillingAdmin", "billing.monitoring@gymos.test");

        using HttpResponseMessage response = await billingAdmin.GetAsync(
            new Uri("/api/v1/monitoring/jobs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownJobIs404()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/monitoring/jobs/does-not-exist/runs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RunsAreNewestFirstAndTheLimitIsClamped()
    {
        DateTimeOffset now = Clock.UtcNow;
        for (int i = 0; i < 205; i++)
        {
            await SeedAsync("retention", now.AddMinutes(-i), JobRunStatus.Succeeded, error: null);
        }

        JsonElement page = await GetAsync("/api/v1/monitoring/jobs/retention/runs?limit=500");

        Assert.Equal(200, page.GetProperty("runs").GetArrayLength());
        Assert.True(page.GetProperty("hasMore").GetBoolean());
        DateTimeOffset first = page.GetProperty("runs")[0].GetProperty("startedAtUtc").GetDateTimeOffset();
        DateTimeOffset second = page.GetProperty("runs")[1].GetProperty("startedAtUtc").GetDateTimeOffset();
        Assert.True(first > second);
    }

    [Fact]
    public async Task AnInvalidStatusIs400()
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri("/api/v1/monitoring/jobs/subscription-renewal/runs?status=Exploded", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<JsonElement> GetAsync(string relativeUrl)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(relativeUrl, UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task SeedAsync(string jobName, DateTimeOffset startedAt, JobRunStatus status, string? error)
    {
        await using AppDbContext context = _host.CreateDbContext();
        JobRun run = JobRun.Start(Guid.NewGuid(), jobName, "seed", startedAt);
        context.Set<JobRun>().Add(run);
        await context.SaveChangesAsync();

        if (status != JobRunStatus.Running)
        {
            await context.Database.ExecuteSqlAsync($"""
                UPDATE platform.job_runs
                SET status = {(byte)status}, finished_at_utc = {startedAt.AddSeconds(2)}, duration_ms = 2000, error = {error}
                WHERE id = {run.Id}
                """);
        }
    }
}
