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

        // A tripwire, not a specification: this number is expected to change whenever an endpoint
        // is added, and the point of asserting it is that adding one makes you look at this test
        // and confirm the new operation landed in `secured` rather than in `anonymous`.
        // 43 → 49 across M4: two feature-catalogue operations, two plan-feature operations, two
        // gym-feature operations.
        Assert.Equal(49, secured.Count);
    }

    /// <summary>
    /// The contract must declare the headers the pipeline reads, with the same requiredness the
    /// pipeline actually applies. <c>versionRequired</c> on the command's <c>ApplyExpectedVersion</c>
    /// call is the source of truth for <c>If-Match</c>, and it deliberately varies: a <c>PUT</c> that
    /// overwrites the whole resource requires it (a lost update silently overwrites a concurrent
    /// edit), while an action endpoint like mark-paid, void, or a lifecycle transition only honours
    /// it when supplied — the operation is narrow enough, or already guarded by its own state
    /// machine, that a missing precondition is not a correctness problem. Declaring an honoured
    /// header as required would overstate enforcement — the same defect this task removes, just
    /// inverted — so this theory pins both directions rather than only the required one.
    /// <c>Idempotency-Key</c> and <c>X-Confirm-Action</c> are always required: the idempotency
    /// middleware rejects a missing key outright (<c>MarkInvoicePaidCommand.KeyRequired</c> is
    /// unconditionally <see langword="true"/>), and the confirmation comparison never matches a
    /// <see langword="null"/> value, so both are enforced on every call regardless of state.
    /// </summary>
    [Theory]
    [InlineData("/api/v1/invoices/{invoiceId}/mark-paid", "post", "Idempotency-Key", true)]
    [InlineData("/api/v1/invoices/{invoiceId}/mark-paid", "post", "If-Match", false)]
    [InlineData("/api/v1/invoices/{invoiceId}/void", "post", "X-Confirm-Action", true)]
    [InlineData("/api/v1/invoices/{invoiceId}/void", "post", "If-Match", false)]
    [InlineData("/api/v1/plans/{planId}", "put", "If-Match", true)]
    [InlineData("/api/v1/gyms/{gymId}", "delete", "X-Confirm-Action", true)]
    [InlineData("/api/v1/plans/{planId}/activate", "post", "If-Match", false)]

    // The price routes: required since the 2026-08-11 audit made them so (they overwrite a price
    // the caller read, and SetPrice is an upsert with no state machine to refuse a stale write).
    // Added here because the audit changed the pipeline and left the declaration saying "honoured"
    // — this theory's hand-picked cases did not cover these paths, so nothing caught the drift.
    [InlineData("/api/v1/plans/{planId}/prices", "put", "If-Match", true)]
    [InlineData("/api/v1/plans/{planId}/prices/{planPriceId}", "delete", "If-Match", true)]

    // M4: both writes overwrite what the caller read.
    [InlineData("/api/v1/plans/{planId}/features", "put", "If-Match", true)]
    [InlineData("/api/v1/features/{featureId}", "put", "If-Match", true)]
    public async Task EnforcedHeaders_AreDeclared_WithTheRequirednessThePipelineActuallyApplies(
        string path, string method, string header, bool expectedRequired)
    {
        JsonElement document = await GetOpenApiDocumentAsync();

        JsonElement operation = document.GetProperty("paths").GetProperty(path).GetProperty(method);

        JsonElement parameter = operation.GetProperty("parameters").EnumerateArray().Single(
            candidate =>
                candidate.GetProperty("in").GetString() == "header" &&
                candidate.GetProperty("name").GetString() == header);

        // `required` is false by default in OpenAPI, so the serializer omits the field rather than
        // writing `false` — an absent property means "not required", not "unspecified".
        bool actualRequired = parameter.TryGetProperty("required", out JsonElement requiredElement)
            && requiredElement.GetBoolean();
        Assert.Equal(expectedRequired, actualRequired);
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
