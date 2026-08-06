using System.Net;
using Xunit;

namespace GymOS.IntegrationTests;

[Collection(PostgresCollectionDefinition.Name)]
public sealed class IdempotencyHostTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public IdempotencyHostTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("idempotency_host");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Host_boots_and_serves_health_with_the_idempotency_table_migrated()
    {
        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task A_request_with_an_idempotency_key_on_a_non_idempotent_endpoint_is_harmless()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/ready", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "smoke-1");
        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Idempotency-Replayed"));
    }
}
