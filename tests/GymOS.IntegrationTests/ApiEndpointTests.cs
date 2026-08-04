using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The real HTTP host (WebApplicationFactory) against the real database: health
/// endpoints, correlation spine, OpenAPI document.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ApiEndpointTests : IAsyncLifetime
{
    private const string CorrelationHeader = "X-Correlation-Id";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ApiEndpointTests(PostgresFixture postgres) => _postgres = postgres;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Database", _postgres.ConnectionString));
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task HealthLive_Returns200_WithoutTouchingDependencies()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task HealthReady_Returns200_AgainstTheRealDatabase()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SuppliedCorrelationId_IsEchoedOnTheResponse()
    {
        var supplied = Guid.NewGuid();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, supplied.ToString());

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(supplied.ToString(), Assert.Single(response.Headers.GetValues(CorrelationHeader)));
    }

    [Fact]
    public async Task MissingCorrelationId_GetsAFreshOneMinted()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/health/live", UriKind.Relative));

        string minted = Assert.Single(response.Headers.GetValues(CorrelationHeader));
        Assert.NotEqual(Guid.Empty, Guid.Parse(minted));
    }

    [Fact]
    public async Task InvalidCorrelationId_IsReplaced_NeverEchoedBack()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/live", UriKind.Relative));
        request.Headers.Add(CorrelationHeader, "not-a-guid'); DROP TABLE outbox.messages;--");

        HttpResponseMessage response = await _client.SendAsync(request);

        string returned = Assert.Single(response.Headers.GetValues(CorrelationHeader));
        Assert.True(Guid.TryParse(returned, out _));
    }

    [Fact]
    public async Task OpenApiDocument_IsServed()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body, StringComparison.Ordinal);
    }
}
