using System.Net;
using System.Text.Json;
using GymOS.Platform.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Rules R11 and R12 enforced against the real endpoint graph (ARCHITECTURE-ADDENDUM.md §8).
///
/// These run over <see cref="EndpointDataSource"/> rather than over source code, so they see
/// what the host actually mapped. A new endpoint inherits both rules with no test to write —
/// which is the point: an unprotected or undocumented endpoint should be impossible to ship
/// by omission.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class EndpointConventionTests : IAsyncLifetime
{
    /// <summary>
    /// Endpoints that are anonymous on purpose. Each entry is a decision someone made, and
    /// adding one requires editing this list — which is where the argument happens.
    /// </summary>
    private static readonly HashSet<string> DeliberatelyAnonymous = new(StringComparer.Ordinal)
    {
        "/api/v{version:apiVersion}/auth/login",   // how a caller obtains permissions
        "/api/v{version:apiVersion}/auth/refresh", // authorized by the refresh cookie

        // ADR-53. The person following an invitation link cannot sign in — that is the situation
        // these exist to resolve — so the one-time token in the link is the credential. Both the
        // GET (is this link still usable?) and the POST (set the password) share the pattern.
        "/api/v{version:apiVersion}/auth/set-password",
    };

    /// <summary>
    /// Authenticated but permission-free: they act on the caller's own account, so holding a
    /// valid token *is* the authorization.
    /// </summary>
    private static readonly HashSet<string> SelfServiceEndpoints = new(StringComparer.Ordinal)
    {
        "/api/v{version:apiVersion}/auth/logout",
        "/api/v{version:apiVersion}/auth/me",
        "/api/v{version:apiVersion}/auth/change-password",
    };

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public EndpointConventionTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("endpoint_convention_tests");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public void EveryApiEndpoint_DeclaresAPermissionOrAnExplicitExemption()
    {
        var undeclared = new List<string>();

        foreach (RouteEndpoint endpoint in ApiEndpoints())
        {
            string pattern = endpoint.RoutePattern.RawText!;

            if (DeliberatelyAnonymous.Contains(pattern) || SelfServiceEndpoints.Contains(pattern))
            {
                continue;
            }

            bool declaresPermission = endpoint.Metadata
                .OfType<IAuthorizeData>()
                .Any(data => !string.IsNullOrWhiteSpace(data.Policy));

            if (!declaresPermission)
            {
                undeclared.Add($"{string.Join('/', HttpMethodsOf(endpoint))} {pattern}");
            }
        }

        Assert.True(
            undeclared.Count == 0,
            "Endpoints with no permission policy (rule R11 — declare one, or add it to the " +
            $"exemption list with a reason): {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void EveryEndpointPolicyNamesADeclaredPermission()
    {
        // PermissionPolicyProvider builds a policy for any name shaped like a permission key, so
        // a typo in RequireAuthorization("users.mange") produces a requirement nobody can ever
        // satisfy — a 403 for every caller, on an endpoint that looks correctly protected. There
        // is no runtime signal for that, which is exactly why it needs a build-time one.
        var declared = PlatformPermissions.All.Select(permission => permission.Key)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = ApiEndpoints()
            .SelectMany(endpoint => endpoint.Metadata.OfType<IAuthorizeData>()
                .Select(data => data.Policy)
                .Where(policy => !string.IsNullOrWhiteSpace(policy))
                .Select(policy => (Endpoint: endpoint.RoutePattern.RawText!, Policy: policy!)))
            .Where(entry => !declared.Contains(entry.Policy))
            .Select(entry => $"{entry.Endpoint} → '{entry.Policy}'")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "Endpoints requiring permissions that no module declares (fix the name, or declare " +
            $"it in PlatformPermissions): {string.Join(", ", unknown)}");
    }

    [Fact]
    public void TheAnonymousExemptionList_ContainsOnlyEndpointsThatStillExist()
    {
        // A stale exemption is worse than none: it silently excuses whatever route later
        // takes that path.
        var patterns = ApiEndpoints()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToHashSet(StringComparer.Ordinal);

        var stale = DeliberatelyAnonymous.Concat(SelfServiceEndpoints)
            .Where(pattern => !patterns.Contains(pattern))
            .ToList();

        Assert.True(stale.Count == 0, "Exempted endpoints that no longer exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void SelfServiceEndpoints_StillRequireAuthentication()
    {
        var unauthenticated = SelfServiceEndpoints
            .Select(pattern => ApiEndpoints().First(endpoint => endpoint.RoutePattern.RawText == pattern))
            .Where(endpoint => endpoint.Metadata.OfType<IAuthorizeData>().ToList().Count == 0)
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .ToList();

        // "No permission required" must never quietly become "no authentication required".
        Assert.True(
            unauthenticated.Count == 0,
            "Self-service endpoints missing RequireAuthorization: " + string.Join(", ", unauthenticated));
    }

    [Fact]
    public async Task EveryApiOperation_IsDocumentedInTheOpenApiDocument()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var undocumented = new List<string>();

        foreach (JsonProperty path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (JsonProperty operation in path.Value.EnumerateObject())
            {
                bool described =
                    operation.Value.TryGetProperty("summary", out JsonElement summary) &&
                    !string.IsNullOrWhiteSpace(summary.GetString()) &&
                    operation.Value.TryGetProperty("description", out JsonElement description) &&
                    !string.IsNullOrWhiteSpace(description.GetString());

                bool hasResponses =
                    operation.Value.TryGetProperty("responses", out JsonElement responses) &&
                    responses.EnumerateObject().Any();

                if (!described || !hasResponses)
                {
                    undocumented.Add($"{operation.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        Assert.True(
            undocumented.Count == 0,
            "Operations missing a summary, description, or response schemas (rule R12 — the " +
            $"generated clients are built from this document): {string.Join(", ", undocumented)}");
    }

    [Fact]
    public async Task TheOpenApiDocumentReportsConcreteVersionedPaths()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        string document = await response.Content.ReadAsStringAsync();

        // Publishing the raw template would give every generated client a `version` path
        // parameter for a value the document itself fixes (AD-12/AD-37).
        Assert.DoesNotContain("v{version", document, StringComparison.Ordinal);
        Assert.Contains("/api/v1/", document, StringComparison.Ordinal);
    }

    private IEnumerable<RouteEndpoint> ApiEndpoints() =>
        _host.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true);

    private static IEnumerable<string> HttpMethodsOf(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods ?? [];
}
