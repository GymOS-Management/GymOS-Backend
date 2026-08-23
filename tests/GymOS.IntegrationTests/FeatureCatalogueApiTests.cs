using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Infrastructure.Persistence;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The catalogue over real HTTP: what the seeder produced, the adoption count computed through all
/// three resolution layers in SQL, and the precondition on the one write with a platform-wide blast
/// radius.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class FeatureCatalogueApiTests : IAsyncLifetime
{
    private static readonly Uri Features = new("/api/v1/features", UriKind.Relative);

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public FeatureCatalogueApiTests(SqlServerFixture postgres) => _sqlServer = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("features_api");
        // Jobs off. This suite asserts on stored gym_feature_overrides rows, and
        // FeatureOverrideSweepJob runs daily at 03:50 UTC — a CI run alive across that
        // minute would have the sweep withdraw the very exceptions these assertions
        // depend on, as an unreproducible failure that looks like a real defect.
        // FeatureOverrideSweepTests does the same, for the same reason.
        _host = AuthenticationHost.Create(
            connectionString, settings => settings["Jobs:Enabled"] = "false");
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task The_catalogue_is_seeded_and_reports_every_declared_feature()
    {
        JsonElement catalogue = await CatalogueAsync();

        Assert.Equal(PlatformFeatures.All.Count, catalogue.GetArrayLength());

        // Ordered by category then name, so the console can render it without sorting.
        var categories = catalogue.EnumerateArray()
            .Select(feature => feature.GetProperty("category").GetString()!)
            .ToList();
        Assert.Equal(categories.Order(StringComparer.Ordinal), categories);

        JsonElement pos = FeatureByKey(catalogue, PlatformFeatures.Pos);
        Assert.True(pos.GetProperty("defaultEnabled").GetBoolean());
        Assert.False(pos.GetProperty("isPremium").GetBoolean());
        Assert.NotNull(pos.GetProperty("rowVersion").GetString());
    }

    [Fact]
    public async Task Adoption_counts_gyms_that_resolve_the_feature_on_through_every_layer()
    {
        // CRM is declared off by default and premium — so with no plan and no override, nobody has
        // it, which makes it the clean subject for counting each layer's contribution in turn.
        JsonElement before = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(0, before.GetProperty("adoptionCount").GetInt32());

        // Layer 3 stays silent: a gym with no subscription resolves CRM to the platform default (off).
        Guid defaultsGym = await PlatformApiHelpers.CreateGymAsync(_client, "defaults-gym");
        Assert.Equal(0, FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm)
            .GetProperty("adoptionCount").GetInt32());

        // Layer 2: a plan that includes CRM, and a gym subscribed to it.
        Guid planId = (await PlatformApiHelpers.CreatePlanAsync(_client, "WITHCRM")).Id;
        await SetPlanFeatureAsync(planId, PlatformFeatures.Crm, enabled: true);
        Guid planGym = await PlatformApiHelpers.CreateGymAsync(_client, "plan-gym");
        using HttpResponseMessage assigned = await PlatformApiHelpers.AssignAsync(_client, planGym, planId);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        Assert.Equal(1, FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm)
            .GetProperty("adoptionCount").GetInt32());

        // Layer 1: an override on the gym that has no plan at all.
        await SetGymFeatureAsync(defaultsGym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement withOverride = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(2, withOverride.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(1, withOverride.GetProperty("overrideCount").GetInt32());

        // And an override the other way removes a gym the plan would have counted — the case a
        // count of plan rows alone would get wrong.
        await SetGymFeatureAsync(planGym, PlatformFeatures.Crm, enabled: false, "Gym asked to disable.");

        JsonElement afterOptOut = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(1, afterOptOut.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(2, afterOptOut.GetProperty("overrideCount").GetInt32());
    }

    [Fact]
    public async Task A_deleted_gyms_exception_counts_towards_neither_number()
    {
        // Both figures sit in the same catalogue row, so they have to describe the same population.
        // Adoption already excluded deleted gyms; overrideCount counted every row, which meant
        // "3 gyms hold an exception" could include tenants that no longer exist.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "counted-then-deleted");
        await SetGymFeatureAsync(gym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement live = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(1, live.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(1, live.GetProperty("overrideCount").GetInt32());

        await PlatformApiHelpers.DeleteGymAsync(_client, gym, "counted-then-deleted");

        JsonElement dead = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(0, dead.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(0, dead.GetProperty("overrideCount").GetInt32());
    }

    [Fact]
    public async Task A_default_on_feature_counts_every_gym_that_never_decided()
    {
        // POS ships on. Two gyms, neither with a plan or an override: both adopt it by default.
        await PlatformApiHelpers.CreateGymAsync(_client, "pos-gym-one");
        await PlatformApiHelpers.CreateGymAsync(_client, "pos-gym-two");

        Assert.Equal(2, FeatureByKey(await CatalogueAsync(), PlatformFeatures.Pos)
            .GetProperty("adoptionCount").GetInt32());
    }

    [Fact]
    public async Task Editing_a_feature_requires_If_Match_and_never_changes_the_key()
    {
        JsonElement crm = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Guid featureId = crm.GetProperty("id").GetGuid();
        string rowVersion = crm.GetProperty("rowVersion").GetString()!;

        // Without the precondition: refused. Changing defaultEnabled re-answers the feature for
        // every gym that never decided, so a blind overwrite is exactly what must not happen.
        using HttpResponseMessage without = await _client.PutAsJsonAsync(
            FeatureUri(featureId),
            new { name = "CRM", description = "d", category = "Members", defaultEnabled = true, isPremium = true });
        Assert.Equal(HttpStatusCode.PreconditionRequired, without.StatusCode);
        Assert.Equal("concurrency.version_required", await AuthApi.ReadProblemCodeAsync(without));

        using HttpResponseMessage updated = await PutFeatureAsync(
            featureId, rowVersion, "Member CRM", "Leads and follow-ups.", "Members", true, true);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        JsonElement body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Member CRM", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("defaultEnabled").GetBoolean());
        Assert.Equal(PlatformFeatures.Crm, body.GetProperty("key").GetString()); // untouched

        // A stale version is refused rather than applied last-write-wins.
        using HttpResponseMessage stale = await PutFeatureAsync(
            featureId, rowVersion, "Something else", null, null, false, false);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Turning_a_default_on_adopts_every_undecided_gym_at_once()
    {
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "undecided-gym");

        JsonElement crm = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(0, crm.GetProperty("adoptionCount").GetInt32());

        using HttpResponseMessage flipped = await PutFeatureAsync(
            crm.GetProperty("id").GetGuid(),
            crm.GetProperty("rowVersion").GetString()!,
            "CRM", "Leads.", "Members", defaultEnabled: true, isPremium: true);
        Assert.Equal(HttpStatusCode.OK, flipped.StatusCode);

        // The blast radius the endpoint's description warns about, demonstrated: one edit, and a
        // gym nobody touched now resolves CRM on.
        Assert.Equal(1, FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm)
            .GetProperty("adoptionCount").GetInt32());

        // Nothing was written to the gym itself — it holds no override and no plan, so the answer
        // changed underneath it. That is the point, and it is why this write requires If-Match.
        using HttpResponseMessage resolved = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gym}/features", UriKind.Relative));
        JsonElement forGym = FeatureByKey(
            await resolved.Content.ReadFromJsonAsync<JsonElement>(), PlatformFeatures.Crm);

        Assert.True(forGym.GetProperty("enabled").GetBoolean());
        Assert.Equal("Default", forGym.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Reading_needs_features_read_and_editing_needs_features_manage()
    {
        // SupportAgent holds features.read only — it answers "why is this off?" without being able
        // to change what the platform ships.
        HttpClient supportAgent = await PlatformApiHelpers.SignedInAsync(
            _host, _client, "SupportAgent", "support.features@gymos.test");

        using HttpResponseMessage read = await supportAgent.GetAsync(Features);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        JsonElement crm = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        using var request = new HttpRequestMessage(
            HttpMethod.Put, FeatureUri(crm.GetProperty("id").GetGuid()))
        {
            Content = JsonContent.Create(new
            {
                name = "CRM",
                description = "d",
                category = "Members",
                defaultEnabled = true,
                isPremium = true,
            }),
        };
        request.Headers.TryAddWithoutValidation(
            "If-Match", $"\"{crm.GetProperty("rowVersion").GetString()}\"");

        using HttpResponseMessage write = await supportAgent.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        supportAgent.Dispose();
    }

    [Fact]
    public async Task An_override_carries_who_set_it_and_when()
    {
        // The only compensating control while overrides never expire: "how old is this grant, and
        // who made it" without going to the audit log.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "override-provenance-gym");
        await SetGymFeatureAsync(gym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement crm = FeatureApiHelpers.Feature(
            await FeatureApiHelpers.ReadFeaturesAsync(_client, gym), PlatformFeatures.Crm);
        Assert.Equal(
            AuthenticationHost.SuperAdminEmail, crm.GetProperty("overrideSetByEmail").GetString());
        Assert.NotNull(crm.GetProperty("overrideSetAtUtc").GetString());
    }

    [Fact]
    public async Task A_feature_the_code_no_longer_declares_is_marked_undeclared()
    {
        // Insert a row for a key no module declares, exactly as a rename would leave behind — the
        // seeder never deletes an orphan (dropping it would cascade-delete plan contents and gym
        // overrides that still reference it), so without this flag the row reads as a live feature.
        const string OrphanKey = "legacy_thing";
        await InsertUndeclaredFeatureAsync(OrphanKey);

        JsonElement catalogue = await CatalogueAsync();
        Assert.False(FeatureByKey(catalogue, OrphanKey).GetProperty("isDeclared").GetBoolean());
        Assert.True(FeatureByKey(catalogue, PlatformFeatures.Pos).GetProperty("isDeclared").GetBoolean());
    }

    [Fact]
    public async Task Editing_a_feature_does_not_pay_for_adoption_counts_on_the_read_back()
    {
        // PUT /features/{id} shares FeatureListItem's shape with the list endpoint, but not its
        // cost: the read-back skips the O(features x gyms) adoption scan and reports null for both
        // counts — null, not zero, because a feature with real adopters does not lose them the
        // moment its description is edited, and 0 would say otherwise with no way to tell it apart
        // from a genuine zero. GET /features is where the counts are authoritative, and this proves
        // it still pays for and returns the real numbers, unaffected by the edit that just happened.
        Guid gym = await PlatformApiHelpers.CreateGymAsync(_client, "write-response-gym");
        await SetGymFeatureAsync(gym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement crm = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(1, crm.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(1, crm.GetProperty("overrideCount").GetInt32());

        using HttpResponseMessage updated = await PutFeatureAsync(
            crm.GetProperty("id").GetGuid(),
            crm.GetProperty("rowVersion").GetString()!,
            "CRM", "Leads.", "Members", defaultEnabled: false, isPremium: true);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        JsonElement body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("adoptionCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("overrideCount").ValueKind);

        // The list endpoint is unaffected: it still pays for, and returns, the real counts.
        JsonElement afterEdit = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(1, afterEdit.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(1, afterEdit.GetProperty("overrideCount").GetInt32());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static Uri FeatureUri(Guid featureId) =>
        new($"/api/v1/features/{featureId}", UriKind.Relative);

    private static JsonElement FeatureByKey(JsonElement catalogue, string key) =>
        catalogue.EnumerateArray().Single(feature => feature.GetProperty("key").GetString() == key);

    private async Task<JsonElement> CatalogueAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(Features);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<HttpResponseMessage> PutFeatureAsync(
        Guid featureId,
        string rowVersion,
        string name,
        string? description,
        string? category,
        bool defaultEnabled,
        bool isPremium)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, FeatureUri(featureId))
        {
            Content = JsonContent.Create(new { name, description, category, defaultEnabled, isPremium }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        return await _client.SendAsync(request);
    }

    /// <summary>
    /// Sets one plan feature through the real endpoint. This drove the state through raw SQL until
    /// task 4 shipped <c>PUT /plans/{id}/features</c> — going through HTTP now means the adoption
    /// counts above are computed over state the API itself can produce.
    /// </summary>
    private async Task SetPlanFeatureAsync(Guid planId, string featureKey, bool enabled)
    {
        using HttpResponseMessage plan = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        string rowVersion = (await plan.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/features", UriKind.Relative))
        {
            Content = JsonContent.Create(new { features = new[] { new { key = featureKey, isEnabled = enabled } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Same, for a gym override — task 5's <c>PUT /gyms/{id}/features</c>.</summary>
    private async Task SetGymFeatureAsync(Guid gymId, string featureKey, bool enabled, string reason)
    {
        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/features", UriKind.Relative),
            new { features = new[] { new { key = featureKey, isEnabled = enabled, reason } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Writes a feature row for a key no module declares — there is no endpoint that can do this
    /// (§3.5: keys are code-canonical, never runtime-creatable), so the only way to reproduce what a
    /// rename leaves behind is straight into the table, exactly as <c>FeatureCatalogSeeder</c> would
    /// find it on the next boot.
    /// </summary>
    private async Task InsertUndeclaredFeatureAsync(string key)
    {
        await using AppDbContext context = _host.CreateDbContext();

        context.Add(Feature.FromDefinition(new FeatureDefinition(
            key,
            "Legacy thing",
            "Orphaned by a rename; no module reads this key anymore.",
            "Operations",
            DefaultEnabled: false,
            IsPremium: false)));

        await context.SaveChangesAsync();
    }
}
