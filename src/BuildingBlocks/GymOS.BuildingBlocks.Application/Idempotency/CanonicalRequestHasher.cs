using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// SHA-256 over the bound command serialized with alphabetically ordered properties
/// (AD-39 §1.3 "canonicalized request body", decision recorded in the design doc):
/// two requests hash equal iff they mean the same thing after model binding, immune to
/// client-side whitespace and key-order differences that would falsely 422 a legitimate retry.
/// </summary>
public static class CanonicalRequestHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = CreateCanonicalOptions();

    public static string Hash(object request)
    {
        ArgumentNullException.ThrowIfNull(request);

        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(request, request.GetType(), CanonicalOptions);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static JsonSerializerOptions CreateCanonicalOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                static typeInfo =>
                {
                    int order = 0;
                    foreach (JsonPropertyInfo property in
                             typeInfo.Properties.OrderBy(p => p.Name, StringComparer.Ordinal).ToList())
                    {
                        property.Order = order++;
                    }
                },
            },
        };

        var options = new JsonSerializerOptions { TypeInfoResolver = resolver };
        options.MakeReadOnly();
        return options;
    }
}
