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

    [Fact]
    public async Task A_key_longer_than_200_characters_is_rejected_400_key_invalid()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/ready", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", new string('k', 201));

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("idempotency.key_invalid", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_repeated_idempotency_key_header_is_rejected_400_key_invalid()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health/ready", UriKind.Relative));
        request.Headers.Add("Idempotency-Key", "first-value");
        request.Headers.Add("Idempotency-Key", "second-value");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("idempotency.key_invalid", body, StringComparison.Ordinal);
    }
}
