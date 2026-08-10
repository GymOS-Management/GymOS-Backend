using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// OpenAPI generation for versioned endpoints (AD-12/AD-37).
///
/// URL-segment versioning declares routes as <c>/api/v{version:apiVersion}/…</c>, and
/// the generator emits that template verbatim — with <c>version</c> as a required path
/// parameter. Published as-is, every generated client would demand a version argument
/// for a value the document itself already fixes, and the diff gate would compare
/// templates instead of the real paths. This transformer substitutes the concrete
/// version and drops the now-redundant parameter, so the document says exactly what a
/// caller must request.
///
/// <para><b>Authentication is part of the contract.</b> The document also declares the
/// bearer scheme AD-4 mandates and marks every endpoint that enforces it. Without this the
/// document documented 25 different <c>401</c> responses while never saying how to avoid
/// one: generated clients emitted no auth handling, and a reference UI had nothing to bind
/// an "Authorize" control to.</para>
/// </summary>
public static partial class OpenApiExtensions
{
    private const string VersionParameterName = "version";

    /// <summary>Scheme id referenced by every secured operation; also what a UI's authorize control binds to.</summary>
    internal const string BearerSchemeName = "bearerAuth";

    [GeneratedRegex(@"\{version(:apiVersion)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSegment();

    public static IServiceCollection AddGymOSOpenApi(this IServiceCollection services, string documentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentName);

        // "v1" → "1": the route segment is already `v{version}`, so the substituted
        // value must not repeat the prefix.
        string apiVersion = documentName.TrimStart('v', 'V');

        services.AddOpenApi(documentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                SubstituteVersion(document, apiVersion);
                DeclareBearerScheme(document);
                return Task.CompletedTask;
            });

            // Per-operation rather than a blanket document-level requirement: a single root
            // `security` block would claim /auth/login needs a token to obtain a token.
            options.AddOperationTransformer((operation, context, _) =>
            {
                if (RequiresAuthorization(context.Description.ActionDescriptor.EndpointMetadata))
                {
                    operation.Security =
                    [
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference(BearerSchemeName)] = [],
                        },
                    ];
                }

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Whether the document and its reference UI are served at all.
    ///
    /// <para><b>Off outside Development unless an operator says otherwise.</b> The document is a
    /// complete map of every endpoint, parameter and schema; served unauthenticated in production
    /// it is free reconnaissance. It is not a secret — an API's shape is discoverable by other
    /// means — so this is not a security boundary, just the difference between publishing the map
    /// and not. The same opt-in shape as <c>Security:ForwardedHeaders</c>: absent means off, and
    /// turning it on is a deliberate act recorded in configuration.</para>
    ///
    /// <code>
    /// "OpenApi": { "Enabled": true }      // or OpenApi__Enabled=true
    /// </code>
    ///
    /// <para>The default is derived from the environment rather than read from
    /// <c>appsettings.Development.json</c>, so a host that loads no settings file at all — the
    /// integration-test host, which asserts the document against its committed snapshot — still
    /// behaves like a developer machine.</para>
    /// </summary>
    public static bool IsOpenApiExposed(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return configuration.GetValue("OpenApi:Enabled", environment.IsDevelopment());
    }

    /// <summary>
    /// Opens the API reference in a browser when the host starts — see
    /// <see cref="ApiReferenceBrowserLauncher"/> for why this is an application concern rather
    /// than a <c>launchSettings.json</c> one.
    ///
    /// <para>Registered only where it makes sense: the reference has to be published, the
    /// environment has to be Development, and the process must not be containerised. Anything
    /// else and this adds no service at all, so there is nothing to misfire.</para>
    /// </summary>
    public static IServiceCollection AddGymOSApiReferenceBrowser(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // Set by the official .NET base images; the check costs nothing and removes the one
        // deployment where opening a browser is not merely useless but confusing.
        bool inContainer = string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        bool byDefault = IsOpenApiExposed(configuration, environment) &&
            environment.IsDevelopment() &&
            !inContainer;

        if (configuration.GetValue("OpenApi:LaunchBrowser", byDefault))
        {
            services.AddHostedService<ApiReferenceBrowserLauncher>();
        }

        return services;
    }

    /// <summary>
    /// Mirrors the endpoint's own authorization metadata rather than a hand-kept list, so an
    /// endpoint added without <c>RequireAuthorization</c> cannot be documented as secured —
    /// and one added with it cannot be forgotten here.
    /// </summary>
    private static bool RequiresAuthorization(IEnumerable<object> endpointMetadata) =>
        !endpointMetadata.OfType<IAllowAnonymous>().Any() &&
        endpointMetadata.OfType<IAuthorizeData>().Any();

    private static void DeclareBearerScheme(OpenApiDocument document)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);

        document.Components.SecuritySchemes[BearerSchemeName] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description =
                "Access token from POST /api/v1/auth/login, sent as `Authorization: Bearer <token>`. " +
                "Lifetime is 15 minutes; refresh through POST /api/v1/auth/refresh, which reads its " +
                "rotating token from an httpOnly cookie rather than this header.",
        };
    }

    private static void SubstituteVersion(OpenApiDocument document, string apiVersion)
    {
        if (document.Paths is null)
        {
            return;
        }

        // Rebuilt rather than mutated in place: the version lives in the dictionary key.
        var rewritten = new Dictionary<string, IOpenApiPathItem>(StringComparer.Ordinal);

        foreach ((string path, IOpenApiPathItem pathItem) in document.Paths)
        {
            RemoveVersionParameters(pathItem);
            rewritten[VersionSegment().Replace(path, apiVersion)] = pathItem;
        }

        document.Paths.Clear();

        foreach ((string path, IOpenApiPathItem pathItem) in rewritten)
        {
            document.Paths.Add(path, pathItem);
        }

        document.Info?.Version ??= string.Create(CultureInfo.InvariantCulture, $"{apiVersion}.0");
    }

    private static void RemoveVersionParameters(IOpenApiPathItem pathItem)
    {
        if (pathItem.Operations is null)
        {
            return;
        }

        foreach (OpenApiOperation operation in pathItem.Operations.Values)
        {
            if (operation.Parameters is null)
            {
                continue;
            }

            for (int index = operation.Parameters.Count - 1; index >= 0; index--)
            {
                if (string.Equals(operation.Parameters[index].Name, VersionParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    operation.Parameters.RemoveAt(index);
                }
            }
        }
    }
}
