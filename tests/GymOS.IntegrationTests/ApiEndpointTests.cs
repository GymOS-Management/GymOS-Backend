using System.Text.Json;
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

    [Fact]
    public async Task OpenApiDocument_DeclaresTheBearerScheme()
    {
        JsonElement document = await GetOpenApiDocumentAsync();

        JsonElement scheme = document
            .GetProperty("components")
            .GetProperty("securitySchemes")
            .GetProperty("bearerAuth");

        Assert.Equal("http", scheme.GetProperty("type").GetString());
        Assert.Equal("bearer", scheme.GetProperty("scheme").GetString());
        Assert.Equal("JWT", scheme.GetProperty("bearerFormat").GetString());
    }

    /// <summary>
    /// The document must agree with the pipeline about which endpoints need a token. These two
    /// are the only ones that cannot: login issues the token, and refresh reads a rotating token
    /// from an httpOnly cookie rather than the bearer header. Everything else is secured — and a
    /// new endpoint that forgets <c>RequireAuthorization</c> lands in this list and fails here.
    /// </summary>
    [Fact]
    public async Task EveryOperation_DeclaresBearerAuth_ExceptTheTwoThatCannotRequireIt()
    {
        JsonElement document = await GetOpenApiDocumentAsync();

        var anonymous = new List<string>();
        var secured = new List<string>();

        foreach (JsonProperty path in document.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                if (!HttpMethods.Contains(operation.Name))
                {
                    continue;
                }

                string id = $"{operation.Name.ToUpperInvariant()} {path.Name}";

                // Naming the scheme is the whole assertion. `security: [{}]` — an empty
                // requirement object — is how OpenAPI spells "authentication optional", so a
                // check for a non-empty array passes on a document that secures nothing. It
                // did: the first version of this transformer shipped every operation as [{}].
                if (DeclaresBearerAuth(operation.Value))
                {
                    secured.Add(id);
                }
                else
                {
                    anonymous.Add(id);
                }
            }
        }

        Assert.Equal(
            ["POST /api/v1/auth/login", "POST /api/v1/auth/refresh"],
            anonymous.Order(StringComparer.Ordinal));

        Assert.Equal(43, secured.Count);
    }

    private static bool DeclaresBearerAuth(JsonElement operation) =>
        operation.TryGetProperty("security", out JsonElement security) &&
        security.EnumerateArray().Any(requirement =>
            requirement.EnumerateObject().Any(scheme =>
                scheme.Name == "bearerAuth"));

    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    private async Task<JsonElement> GetOpenApiDocumentAsync()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
