using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The real HTTP host (WebApplicationFactory) against the real database: health
/// endpoints, correlation spine, OpenAPI document.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class ApiEndpointTests : IAsyncLifetime
{
    private const string CorrelationHeader = "X-Correlation-Id";

    private readonly SqlServerFixture _sqlServer;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ApiEndpointTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _sqlServer.ConnectionString);

            // No `Database:MigrateOnStartup` here, deliberately, and this used to say the opposite.
            //
            // The history is worth keeping, because it is why the assertion is trustworthy now. This
            // host originally set only the connection string, leaving it on
            // appsettings.Development.json's `MigrateOnStartup: false` — so it migrated nothing and
            // borrowed whatever state an earlier suite had left in the container's shared default
            // database. HealthReady_Returns200_AgainstTheRealDatabase passed anyway, because readiness
            // asked CanConnectAsync, which an empty database answers yes to. Once readiness also began
            // asking whether the schema is current (DatabaseReadinessHealthCheck), that borrowed state
            // became the difference between 200 and 503, and the test would have passed or failed on
            // execution order — so this host was made to migrate itself.
            //
            // It no longer can: the role every host connects as owns nothing and holds no DDL.
            // SqlServerFixture migrates the default database in InitializeAsync instead, so the
            // guarantee this comment has always been about — a real, migrated database, not a borrowed
            // one — now holds for every suite rather than for this one because it asked.
        });

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

    /// <summary>
    /// Anonymous by design: deployment verification runs before anyone can sign in, and a rollback
    /// decision must not depend on the identity provider being healthy. The version is asserted
    /// present rather than equal to a literal — pinning "0.9.0" here would mean every release bump
    /// breaks a test that is about the endpoint working, not about which release this is.
    /// </summary>
    [Fact]
    public async Task Version_IsServedAnonymously_AndNamesTheBuildAndEnvironment()
    {
        HttpResponseMessage response = await _client.GetAsync(new Uri("/version", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("environment").GetString()));

        // Present as properties even when this build stamped neither, so a caller reads null rather
        // than having to distinguish "absent" from "unknown". A test-host build stamps no timestamp.
        Assert.True(root.TryGetProperty("commit", out _));
        Assert.True(root.TryGetProperty("builtAt", out _));
    }

    /// <summary>
    /// <c>/version</c> is an operational endpoint, not part of the client contract, so it must stay
    /// out of the published document — exactly like the health endpoints. Asserted because the
    /// alternative is silent: an <c>ExcludeFromDescription</c> dropped during a refactor would put
    /// it into <c>contracts/openapi-v1.json</c>, and AD-37's diff gate would then report an API
    /// change every time an operational detail moved.
    /// </summary>
    [Fact]
    public async Task Version_IsAbsentFromTheOpenApiDocument()
    {
        JsonElement document = await GetOpenApiDocumentAsync();

        Assert.False(document.GetProperty("paths").TryGetProperty("/version", out _));
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
    public async Task EveryOperation_DeclaresBearerAuth_ExceptTheFewThatCannotRequireIt()
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
            [
                "GET /api/v1/auth/set-password",
                "POST /api/v1/auth/login",
                "POST /api/v1/auth/refresh",
                "POST /api/v1/auth/set-password",
            ],
            anonymous.Order(StringComparer.Ordinal));

        // A tripwire, not a specification: this number is expected to change whenever an endpoint
        // is added, and the point of asserting it is that adding one makes you look at this test
        // and confirm the new operation landed in `secured` rather than in `anonymous`.
        // 43 → 49 across M4: two feature-catalogue operations, two plan-feature operations, two
        // gym-feature operations.
        // 49 → 52 in M5 task 4: the three dashboard operations (summary, health, activity).
        // 52 → 55 in M5 task 5: the three analytics series operations (revenue, gyms-growth, churn).
        // 55 → 57 with ADR-53: resend-invitation and reset-password. The two set-password
        // operations landed in the anonymous list above, which is the answer this tripwire exists
        // to make somebody check.
        // 57 → 60 in M6 task 10: the three monitoring operations (overview, jobs, job runs).
        // 60 → 62 in M7 task 7: the two settings operations (get, update).
        Assert.Equal(62, secured.Count);
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
