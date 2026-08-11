using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// <c>GET/PUT /gyms/{id}/features</c> over real HTTP against real PostgreSQL — the three-layer rule
/// end to end, including the part that has no unit-test equivalent: that a withdrawn override really
/// leaves the table, so the next plan change reaches the gym.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class GymFeatureApiTests : IAsyncLifetime
{
    private const string Reason = "Contractually promised during migration.";

    private static readonly Uri Gyms = new("/api/v1/gyms", UriKind.Relative);
    private static readonly Uri Plans = new("/api/v1/plans", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public GymFeatureApiTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("gym_features_api");
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
    public async Task A_gym_with_no_plan_resolves_every_feature_against_the_platform_default()
    {
        Guid gym = await CreateGymAsync("plain-gym");

        JsonElement features = await ReadFeaturesAsync(gym);

        Assert.Equal(PlatformFeatures.All.Count, features.GetArrayLength());
        Assert.All(
            features.EnumerateArray(),
            feature => Assert.Equal("Default", feature.GetProperty("source").GetString()));

        // POS ships on and CRM ships off, so "everything defaults" is not vacuously true.
        Assert.True(Feature(features, PlatformFeatures.Pos).GetProperty("enabled").GetBoolean());
        Assert.False(Feature(features, PlatformFeatures.Crm).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task An_override_beats_the_plan_and_says_so()
    {
        Guid plan = await CreatePlanAsync("BASIC");
        Guid gym = await CreateGymAsync("override-gym");
        await AssignAsync(gym, plan);

        // The plan excludes CRM outright — not merely silent about it.
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: false);
        Assert.Equal("Plan", await SourceAsync(gym, PlatformFeatures.Crm));
        Assert.False(await EnabledAsync(gym, PlatformFeatures.Crm));

        JsonElement response = await SetGymFeaturesAsync(
            gym, (PlatformFeatures.Crm, true, Reason));

        JsonElement crm = Feature(response, PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Override", crm.GetProperty("source").GetString());
        Assert.Equal(Reason, crm.GetProperty("overrideReason").GetString());

        // Stored, once, with the operator who did it — "who gave this gym a paid feature for free"
        // is one row.
        Assert.Equal(1, await CountOverridesAsync(gym));
        Assert.Equal(AuthenticationHost.SuperAdminEmail, await SetByEmailAsync(gym, PlatformFeatures.Crm));
    }

    [Fact]
    public async Task Asking_for_what_the_plan_gives_deletes_the_override_row()
    {
        Guid plan = await CreatePlanAsync("PRO");
        Guid gym = await CreateGymAsync("selfcleaning-gym");
        await AssignAsync(gym, plan);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, Reason));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // The plan grows to include what the gym was granted by exception.
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);

        JsonElement response = await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, null));

        // The row is gone from the table, not merely ignored — checked in SQL because that is the
        // whole promise: the next plan change reaches this gym instead of being shadowed.
        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(response, PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
        Assert.Null(crm.GetProperty("overrideReason").GetString());
    }

    [Fact]
    public async Task A_withdrawn_override_stops_shadowing_later_plan_changes()
    {
        Guid plan = await CreatePlanAsync("PRO");
        Guid gym = await CreateGymAsync("follows-plan-gym");
        await AssignAsync(gym, plan);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, Reason));
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, null));

        // Now the plan drops CRM. Had the redundant override survived, this gym would still resolve
        // CRM on — the exact frozen-by-a-forgotten-row failure the clear rule exists to prevent.
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: false);

        Assert.False(await EnabledAsync(gym, PlatformFeatures.Crm));
        Assert.Equal("Plan", await SourceAsync(gym, PlatformFeatures.Crm));
    }

    [Fact]
    public async Task An_override_without_a_reason_is_refused_and_nothing_is_written()
    {
        Guid gym = await CreateGymAsync("reasonless-gym");

        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            FeaturesUri(gym),
            new
            {
                features = new object[]
                {
                    new { key = PlatformFeatures.Nutrition, isEnabled = true, reason = "Trial." },
                    new { key = PlatformFeatures.Crm, isEnabled = true, reason = (string?)null },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("features.override_reason_required", await AuthApi.ReadProblemCodeAsync(response));

        // The valid half of the request is not applied either: a caller who cannot tell which half
        // landed is worse off than one who was refused.
        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task An_unknown_key_rejects_the_whole_request()
    {
        Guid gym = await CreateGymAsync("unknown-key-gym");

        using HttpResponseMessage response = await _client.PutAsJsonAsync(
            FeaturesUri(gym),
            new
            {
                features = new[]
                {
                    new { key = PlatformFeatures.Crm, isEnabled = true, reason = Reason },
                    new { key = "teleportation", isEnabled = true, reason = Reason },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("features.unknown_key", await AuthApi.ReadProblemCodeAsync(response));
        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    [Fact]
    public async Task Only_the_keys_sent_are_touched()
    {
        Guid gym = await CreateGymAsync("partial-gym");

        await SetGymFeaturesAsync(
            gym,
            (PlatformFeatures.Crm, true, Reason),
            (PlatformFeatures.Pos, false, "Owner runs their own till."));
        Assert.Equal(2, await CountOverridesAsync(gym));

        // A console that only knows about CRM must not withdraw the POS exception by omission.
        JsonElement response = await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Renewed."));

        Assert.Equal(2, await CountOverridesAsync(gym));
        Assert.False(Feature(response, PlatformFeatures.Pos).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_gym_detail_carries_the_same_resolution()
    {
        Guid gym = await CreateGymAsync("detail-gym");
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, Reason));

        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);

        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();
        JsonElement features = body.GetProperty("features");

        // §3.3 — and byte-identical to the dedicated endpoint, because both call one resolution.
        Assert.Equal(PlatformFeatures.All.Count, features.GetArrayLength());
        Assert.Equal("Override", Feature(features, PlatformFeatures.Crm).GetProperty("source").GetString());
        Assert.Equal(
            JsonSerializer.Serialize(await ReadFeaturesAsync(gym)),
            JsonSerializer.Serialize(features));
    }

    [Fact]
    public async Task Two_gyms_resolve_the_same_feature_differently_for_different_reasons()
    {
        Guid plan = await CreatePlanAsync("WITHCRM");
        await SetPlanFeatureAsync(plan, PlatformFeatures.Crm, isEnabled: true);

        Guid byPlan = await CreateGymAsync("by-plan-gym");
        await AssignAsync(byPlan, plan);

        Guid byOverride = await CreateGymAsync("by-override-gym");
        await SetGymFeaturesAsync(byOverride, (PlatformFeatures.Crm, true, Reason));

        Guid byDefault = await CreateGymAsync("by-default-gym");

        // All three answers are visible at once, and only `source` explains which lever changes each:
        // edit the plan, withdraw the exception, or move the platform default.
        Assert.True(await EnabledAsync(byPlan, PlatformFeatures.Crm));
        Assert.Equal("Plan", await SourceAsync(byPlan, PlatformFeatures.Crm));

        Assert.True(await EnabledAsync(byOverride, PlatformFeatures.Crm));
        Assert.Equal("Override", await SourceAsync(byOverride, PlatformFeatures.Crm));

        Assert.False(await EnabledAsync(byDefault, PlatformFeatures.Crm));
        Assert.Equal("Default", await SourceAsync(byDefault, PlatformFeatures.Crm));
    }

    [Fact]
    public async Task A_deleted_gym_cannot_be_granted_anything()
    {
        Guid gym = await CreateGymAsync("doomed-gym");

        using HttpResponseMessage read = await _client.GetAsync(
            new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));
        string rowVersion = (await read.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;

        using var delete = new HttpRequestMessage(
            HttpMethod.Delete, new Uri($"/api/v1/gyms/{gym}", UriKind.Relative));
        delete.Headers.TryAddWithoutValidation("X-Confirm-Action", "doomed-gym");
        delete.Headers.TryAddWithoutValidation("If-Match", $"\"{rowVersion}\"");
        using HttpResponseMessage deleted = await _client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using HttpResponseMessage refused = await _client.PutAsJsonAsync(
            FeaturesUri(gym),
            new { features = new[] { new { key = PlatformFeatures.Crm, isEnabled = true, reason = Reason } } });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("gyms.deleted", await AuthApi.ReadProblemCodeAsync(refused));

        // Reading still works: an operator investigating a dead tenant needs to see what it had.
        using HttpResponseMessage stillReadable = await _client.GetAsync(FeaturesUri(gym));
        Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);
    }

    [Fact]
    public async Task An_unknown_gym_is_not_found_rather_than_answered_with_defaults()
    {
        var missing = Guid.NewGuid();

        using HttpResponseMessage read = await _client.GetAsync(FeaturesUri(missing));
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal("gyms.not_found", await AuthApi.ReadProblemCodeAsync(read));

        using HttpResponseMessage write = await _client.PutAsJsonAsync(
            FeaturesUri(missing),
            new { features = new[] { new { key = PlatformFeatures.Crm, isEnabled = true, reason = Reason } } });
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
    }

    [Fact]
    public async Task Reading_needs_features_read_and_granting_needs_features_manage()
    {
        Guid gym = await CreateGymAsync("permission-gym");

        // SupportAgent holds features.read: it can explain why a gym cannot do something without
        // holding the authority to hand out the capability.
        using HttpClient supportAgent = await SignedInAsync("SupportAgent", "support.gymfeatures@gymos.test");

        using HttpResponseMessage read = await supportAgent.GetAsync(FeaturesUri(gym));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using HttpResponseMessage write = await supportAgent.PutAsJsonAsync(
            FeaturesUri(gym),
            new { features = new[] { new { key = PlatformFeatures.Crm, isEnabled = true, reason = Reason } } });
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        Assert.Equal(0, await CountOverridesAsync(gym));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static Uri FeaturesUri(Guid gymId) =>
        new($"/api/v1/gyms/{gymId}/features", UriKind.Relative);

    private static JsonElement Feature(JsonElement features, string key) =>
        features.EnumerateArray().Single(feature => feature.GetProperty("key").GetString() == key);

    private async Task<JsonElement> ReadFeaturesAsync(Guid gymId)
    {
        using HttpResponseMessage response = await _client.GetAsync(FeaturesUri(gymId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<bool> EnabledAsync(Guid gymId, string key) =>
        Feature(await ReadFeaturesAsync(gymId), key).GetProperty("enabled").GetBoolean();

    private async Task<string?> SourceAsync(Guid gymId, string key) =>
        Feature(await ReadFeaturesAsync(gymId), key).GetProperty("source").GetString();

    private async Task<JsonElement> SetGymFeaturesAsync(
        Guid gymId,
        params (string Key, bool IsEnabled, string? Reason)[] features)
    {
        using HttpResponseMessage response = await _client.PutAsJsonAsync(
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

    private async Task<int> CountOverridesAsync(Guid gymId)
    {
        await using var context = _host.CreateDbContext();

        return await context.Set<GymFeatureOverride>().CountAsync(over => over.GymId == gymId);
    }

    /// <summary>
    /// The email of whoever set an override, joined through the user table — provenance is only worth
    /// storing if it can be read back as a person.
    /// </summary>
    private async Task<string?> SetByEmailAsync(Guid gymId, string featureKey)
    {
        await using var context = _host.CreateDbContext();

        return await context.Database
            .SqlQuery<string?>($"""
                SELECT u.email AS "Value"
                FROM platform.gym_feature_overrides o
                JOIN platform.features f ON f.id = o.feature_id
                JOIN platform.users u ON u.id = o.set_by_user_id
                WHERE o.gym_id = {gymId} AND f.key = {featureKey}
                """)
            .SingleAsync();
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
            description = "Created by the gym-feature suite",
            trialDays = 0,
            maxMembers = (int?)null,
            maxTrainers = (int?)null,
            maxStaff = (int?)null,
            storageLimitMb = (long?)null,
            sortOrder = 1,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid planId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var priceRequest = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/prices", UriKind.Relative))
        {
            Content = JsonContent.Create(new { billingCycle = "Monthly", currency = "EUR", amount = 49.99m }),
        };
        priceRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{await PlanVersionAsync(planId)}\"");

        using HttpResponseMessage priced = await _client.SendAsync(priceRequest);
        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        return planId;
    }

    private async Task SetPlanFeatureAsync(Guid planId, string featureKey, bool isEnabled)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put, new Uri($"/api/v1/plans/{planId}/features", UriKind.Relative))
        {
            Content = JsonContent.Create(new { features = new[] { new { key = featureKey, isEnabled } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{await PlanVersionAsync(planId)}\"");

        using HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> PlanVersionAsync(Guid planId)
    {
        using HttpResponseMessage plan = await _client.GetAsync(
            new Uri($"/api/v1/plans/{planId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, plan.StatusCode);

        return (await plan.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("rowVersion").GetString()!;
    }

    private async Task AssignAsync(Guid gymId, Guid planId)
    {
        using HttpResponseMessage assigned = await _client.PutAsJsonAsync(
            new Uri($"/api/v1/gyms/{gymId}/subscription", UriKind.Relative),
            new { planId, billingCycle = "Monthly", currency = "EUR" });

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
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
