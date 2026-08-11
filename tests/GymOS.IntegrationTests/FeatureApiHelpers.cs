using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Feature-resolution helpers shared by <see cref="GymFeatureApiTests"/> and
/// <see cref="GymFeatureLifecycleTests"/> — reading a gym's resolved features, writing an override,
/// setting a plan's opinion, and counting the override rows a change left behind. Sibling to
/// <see cref="PlatformApiHelpers"/> rather than folded into it: that file knows nothing about
/// features, and every caller here does.
/// </summary>
internal static class FeatureApiHelpers
{
    private static Uri FeaturesUri(Guid gymId) =>
        new($"/api/v1/gyms/{gymId}/features", UriKind.Relative);

    /// <summary>Picks one feature's resolution out of a <c>GET/PUT .../features</c> response body.</summary>
    public static JsonElement Feature(JsonElement features, string key) =>
        features.EnumerateArray().Single(feature => feature.GetProperty("key").GetString() == key);

    public static async Task<JsonElement> ReadFeaturesAsync(HttpClient client, Guid gymId)
    {
        using HttpResponseMessage response = await client.GetAsync(FeaturesUri(gymId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Writes one or more overrides and asserts the write succeeded, returning the resolution.</summary>
    public static async Task<JsonElement> SetGymFeaturesAsync(
        HttpClient client,
        Guid gymId,
        params (string Key, bool IsEnabled, string? Reason)[] features)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(
            FeaturesUri(gymId),
            new
            {
                features = features
                    .Select(feature => new { key = feature.Key, isEnabled = feature.IsEnabled, reason = feature.Reason })
                    .ToArray(),
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// The number of override rows stored for a gym, read straight from the table — the whole promise
    /// of the self-cleaning rule is that a redundant row does not merely get ignored, it stops existing.
    /// </summary>
    public static async Task<int> CountOverridesAsync(AuthenticationHost host, Guid gymId)
    {
        await using var context = host.CreateDbContext();

        return await context.Set<GymFeatureOverride>().CountAsync(over => over.GymId == gymId);
    }

    public static async Task SetPlanFeatureAsync(HttpClient client, Guid planId, string featureKey, bool isEnabled)
    {
        using HttpResponseMessage plan = await client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);
        string rowVersion = (await plan.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/features", UriKind.Relative))
        {
            Content = JsonContent.Create(new { features = new[] { new { key = featureKey, isEnabled } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
