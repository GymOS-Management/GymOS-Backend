namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/version</c> — which build is answering, and which commit it came from.
///
/// <para><b>Unversioned and outside the API contract, like the health endpoints.</b> It is not
/// under <c>/api/v1</c> and it is deliberately excluded from the OpenAPI document: consoles and
/// mobile clients do not call it, an operator and a deployment pipeline do. Publishing it in
/// <c>contracts/openapi-v1.json</c> would put an operational detail into the frozen client
/// contract that AD-37's diff gate protects, and every future change to it would read as an API
/// change. <c>/health/live</c> and <c>/health/ready</c> are absent from the document for the same
/// reason.</para>
///
/// <para><b>Anonymous, deliberately.</b> Deployment verification runs before anyone can sign in,
/// and a rollback decision during an incident must not depend on an identity provider that may be
/// part of what is broken. What it discloses is a version, a commit SHA and an environment name —
/// three values already in a public-facing image tag and in source control. It reveals no
/// configuration, no connection detail and no user data; the health endpoints hold the same line
/// by returning a status word alone.</para>
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/version", (IHostEnvironment environment) => TypedResults.Ok(
                new VersionResponse(
                    BuildInfo.Version,
                    BuildInfo.Commit,
                    environment.EnvironmentName,
                    BuildInfo.BuildTimestampUtc)))
            .AllowAnonymous()
            .ExcludeFromDescription()
            .WithName("GetVersion");

        return endpoints;
    }
}

/// <summary>
/// What <c>/version</c> answers. <paramref name="Commit"/> and <paramref name="BuiltAt"/> are
/// <see langword="null"/> on a build that stamped neither — a local <c>dotnet run</c>, typically.
/// Null rather than a placeholder string, so a caller cannot mistake "unknown" for a value.
/// </summary>
/// <param name="Version">The assembly version, e.g. <c>0.9.0</c>.</param>
/// <param name="Commit">The full commit SHA, identical to the container image tag.</param>
/// <param name="Environment">
/// <c>ASPNETCORE_ENVIRONMENT</c> — <c>Development</c>, <c>Staging</c> (UAT) or <c>Production</c>.
/// Which database the host is allowed to open follows from it (<c>DatabaseEnvironmentGuard</c>).
/// </param>
/// <param name="BuiltAt">ISO-8601 UTC build timestamp, when the build supplied one.</param>
public sealed record VersionResponse(
    string Version,
    string? Commit,
    string Environment,
    string? BuiltAt);
