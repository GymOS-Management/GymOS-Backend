using System.Globalization;
using System.Text.RegularExpressions;
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
/// </summary>
public static partial class OpenApiExtensions
{
    private const string VersionParameterName = "version";

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
            options.AddDocumentTransformer((document, _, _) =>
            {
                SubstituteVersion(document, apiVersion);
                return Task.CompletedTask;
            }));

        return services;
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
