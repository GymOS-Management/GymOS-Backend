using System.Net;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// A subscription change withdrawing the exceptions it makes redundant — the maintenance M4's
/// override rule was missing (ARCHITECTURE.md §2.4). Every existing override test in
/// <see cref="GymFeatureApiTests"/> moves the override while the layers beneath it stand still, which
/// is exactly why a gym could be granted CRM as a goodwill trial, upgrade onto a plan that already
/// grants it, and keep the now-redundant row — reporting <c>source: Override</c> and a stale reason
/// for a capability it started paying for, then keeping it for free on a later downgrade.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class GymFeatureLifecycleTests : IAsyncLifetime
{
    private const string TrialReason = "3-month goodwill trial.";

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public GymFeatureLifecycleTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("gym_features_lifecycle");
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
    public async Task A_plan_upgrade_withdraws_an_exception_it_has_made_redundant()
    {
        // The test M4 never had. Every existing override test moves the override while the layers
        // beneath stand still, which is exactly why this defect survived six task reviews and a
        // single-pass review.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);

        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("upgrading-gym");
        await AssignAsync(gym, basic);

        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        Assert.Equal(1, await CountOverridesAsync(gym));

        // The upgrade. The plan now grants what the exception granted.
        await AssignAsync(gym, pro);

        Assert.Equal(0, await CountOverridesAsync(gym));

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.True(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
        Assert.Null(crm.GetProperty("overrideReason").GetString());
    }

    [Fact]
    public async Task A_later_downgrade_does_not_resurrect_the_withdrawn_exception()
    {
        // The consequence that costs money: without withdrawal, the stale row wins on the way back
        // down and the gym keeps a capability it stopped paying for.
        Guid basic = await CreatePlanAsync("BASIC");
        await SetPlanFeatureAsync(basic, PlatformFeatures.Crm, isEnabled: false);
        Guid pro = await CreatePlanAsync("PRO");
        await SetPlanFeatureAsync(pro, PlatformFeatures.Crm, isEnabled: true);

        Guid gym = await CreateGymAsync("round-trip-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, TrialReason));
        await AssignAsync(gym, pro);
        await AssignAsync(gym, basic);

        JsonElement crm = Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm);
        Assert.False(crm.GetProperty("enabled").GetBoolean());
        Assert.Equal("Plan", crm.GetProperty("source").GetString());
    }

    [Fact]
    public async Task An_exception_that_still_differs_survives_a_plan_change()
    {
        // The guard against over-eager withdrawal: this one is still doing work.
        Guid basic = await CreatePlanAsync("BASIC");
        Guid other = await CreatePlanAsync("OTHER");
        Guid gym = await CreateGymAsync("still-differs-gym");
        await AssignAsync(gym, basic);
        await SetGymFeaturesAsync(gym, (PlatformFeatures.Crm, true, "Contractual."));

        await AssignAsync(gym, other);

        Assert.Equal(1, await CountOverridesAsync(gym));
        Assert.Equal("Override", Feature(await ReadFeaturesAsync(gym), PlatformFeatures.Crm)
            .GetProperty("source").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────
    // Thin wrappers binding the shared PlatformApiHelpers / FeatureApiHelpers to this fixture's
    // client/host — see GymFeatureApiTests for the same pattern and why the five feature helpers
    // are not copied here.

    private async Task<Guid> CreatePlanAsync(string code) =>
        (await PlatformApiHelpers.CreatePlanAsync(_client, code)).Id;

    private Task<Guid> CreateGymAsync(string slug) =>
        PlatformApiHelpers.CreateGymAsync(_client, slug);

    private Task<HttpResponseMessage> AssignAsync(Guid gymId, Guid planId) =>
        PlatformApiHelpers.AssignAsync(_client, gymId, planId);

    private Task SetPlanFeatureAsync(Guid planId, string featureKey, bool isEnabled) =>
        FeatureApiHelpers.SetPlanFeatureAsync(_client, planId, featureKey, isEnabled);

    private Task<JsonElement> SetGymFeaturesAsync(
        Guid gymId,
        params (string Key, bool IsEnabled, string? Reason)[] features) =>
        FeatureApiHelpers.SetGymFeaturesAsync(_client, gymId, features);

    private Task<int> CountOverridesAsync(Guid gymId) =>
        FeatureApiHelpers.CountOverridesAsync(_host, gymId);

    private Task<JsonElement> ReadFeaturesAsync(Guid gymId) =>
        FeatureApiHelpers.ReadFeaturesAsync(_client, gymId);

    private static JsonElement Feature(JsonElement features, string key) =>
        FeatureApiHelpers.Feature(features, key);
}
