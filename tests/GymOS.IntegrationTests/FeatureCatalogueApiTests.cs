using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The catalogue over real HTTP: what the seeder produced, the adoption count computed through all
/// three resolution layers in SQL, and the precondition on the one write with a platform-wide blast
/// radius.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class FeatureCatalogueApiTests : IAsyncLifetime
{
    private static readonly Uri Features = new("/api/v1/features", UriKind.Relative);
    private static readonly Uri Gyms = new("/api/v1/gyms", UriKind.Relative);
    private static readonly Uri Plans = new("/api/v1/plans", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public FeatureCatalogueApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("features_api");
        _host = AuthenticationHost.Create(connectionString);
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
        Guid defaultsGym = await CreateGymAsync("defaults-gym");
        Assert.Equal(0, FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm)
            .GetProperty("adoptionCount").GetInt32());

        // Layer 2: a plan that includes CRM, and a gym subscribed to it.
        Guid planId = await CreatePlanAsync("WITHCRM");
        await SetPlanFeatureAsync(planId, PlatformFeatures.Crm, enabled: true);
        Guid planGym = await CreateGymAsync("plan-gym");
        await AssignAsync(planGym, planId);

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
        Guid gym = await CreateGymAsync("counted-then-deleted");
        await SetGymFeatureAsync(gym, PlatformFeatures.Crm, enabled: true, "Goodwill trial.");

        JsonElement live = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(1, live.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(1, live.GetProperty("overrideCount").GetInt32());

        await DeleteGymAsync(gym, "counted-then-deleted");

        JsonElement dead = FeatureByKey(await CatalogueAsync(), PlatformFeatures.Crm);
        Assert.Equal(0, dead.GetProperty("adoptionCount").GetInt32());
        Assert.Equal(0, dead.GetProperty("overrideCount").GetInt32());
    }

    [Fact]
    public async Task A_default_on_feature_counts_every_gym_that_never_decided()
    {
        // POS ships on. Two gyms, neither with a plan or an override: both adopt it by default.
        await CreateGymAsync("pos-gym-one");
        await CreateGymAsync("pos-gym-two");

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
        Guid gym = await CreateGymAsync("undecided-gym");

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
        HttpClient supportAgent = await SignedInAsync("SupportAgent", "support.features@gymos.test");

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

    private async Task<Guid> CreateGymAsync(string slug)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Gyms, new
        {
            slug,
            name = $"Gym {slug}",
            ownerName = "Ada Owner",
            ownerEmail = "ada@acme.test",
            phone = (string?)null,
            countryCode = "CY",
            timeZoneId = "Asia/Nicosia",
            currency = "EUR",
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreatePlanAsync(string code)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Plans, new
        {
            code,
            name = $"Plan {code}",
            description = "Created by the feature suite",
            trialDays = 0,
            maxMembers = (int?)null,
            maxTrainers = (int?)null,
            maxStaff = (int?)null,
            storageLimitMb = (long?)null,
            sortOrder = 1,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid planId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using HttpResponseMessage plan = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        string rowVersion = (await plan.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var priceRequest = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency = "EUR", amount = 49.99m }),
        };
        priceRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        using HttpResponseMessage priced = await _client.SendAsync(priceRequest);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        return planId;
    }

    private async Task AssignAsync(Guid gymId, Guid planId)
    {
        using HttpResponseMessage assigned = await _client.PutAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription", UriKind.Relative),
            new { planId, billingCycle = "Monthly", currency = "EUR" });

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
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

    /// <summary>
    /// Soft-deletes a gym through the real endpoint, confirmation header and all — the row survives,
    /// which is exactly why the counts have to decide what to do about it.
    /// </summary>
    private async Task DeleteGymAsync(Guid gymId, string slug)
    {
        using HttpResponseMessage read = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        string rowVersion = (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gymId}", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("X-Confirm-Action", slug);
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Same, for a gym override — task 5's <c>PUT /gyms/{id}/features</c>.</summary>
    private async Task SetGymFeatureAsync(Guid gymId, string featureKey, bool enabled, string reason)
    {
        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/features", UriKind.Relative),
            new { features = new[] { new { key = featureKey, isEnabled = enabled, reason } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpClient> SignedInAsync(string roleName, string email)
    {
        const string Password = "role-holder-integration-password";

        await using AuthenticationHost seedHost = AuthenticationHost.Create(
            _host.ConnectionString,
            settings =>
            {
                settings["Seed:SuperAdmin:Email"] = email;
                settings["Seed:SuperAdmin:Password"] = Password;
            });

        using HttpClient seedClient = seedHost.CreateClient();
        using HttpResponseMessage ready = await seedClient.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        Guid userId = await FindUserIdAsync(email);
        await SetRolesAsync(userId, await RoleIdAsync(roleName));

        HttpClient client = _host.CreateClient();
        using HttpResponseMessage signIn = await AuthApi.LoginAsync(client, email, Password);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        AuthApi.Authorize(client, await AuthApi.ReadAccessTokenAsync(signIn));

        return client;
    }

    private async Task<Guid> RoleIdAsync(string roleName)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/api/v1/roles", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == roleName)
            .GetProperty("id").GetGuid();
    }

    private async Task<Guid> FindUserIdAsync(string email)
    {
        using HttpResponseMessage response = await _client.GetAsync(
            new Uri($"/api/v1/users?search={Uri.EscapeDataString(email)}", UriKind.Relative));
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("items").EnumerateArray()
            .Single(user => user.GetProperty("email").GetString() == email)
            .GetProperty("id").GetGuid();
    }

    private async Task SetRolesAsync(Guid userId, params Guid[] roleIds)
    {
        using HttpResponseMessage current = await _client.GetAsync(
            new Uri($"/api/v1/users/{userId}", UriKind.Relative));
        using JsonDocument currentBody = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
        string rowVersion = currentBody.RootElement.GetProperty("rowVersion").GetString()!;

        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/users/{userId}/roles", UriKind.Relative))
        {
            Content = JsonContent.Create(new { roleIds }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");

        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
