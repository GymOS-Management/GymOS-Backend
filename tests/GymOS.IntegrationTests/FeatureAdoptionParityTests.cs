using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GymOS.Platform.Domain.Features;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Pins <c>FeatureQueries.Project()</c>'s SQL adoption count against
/// <see cref="FeatureResolution.Resolve"/> — the domain function it deliberately restates rather than
/// calls, because counting adoption per gym in C# would mean loading every gym. The two must never be
/// allowed to disagree: the deferred override-expiry work adds a fourth input to <c>Resolve</c>, and
/// without this test the SQL restatement could silently omit it — the wrong number in exactly the
/// field a retirement decision ("can we retire this feature?") is made from.
///
/// <para>Builds one gym per state the M4.1 design's §7 verification plan names: no subscription, a
/// plan that includes, a plan that excludes, a plan that is silent, an override each way, and a
/// deleted gym. For every one of the twelve declared features, the expected adoption count is
/// computed by calling <see cref="FeatureResolution.Resolve"/> directly, in C#, against each live
/// gym's known (override, plan, default) triple — then compared against what
/// <c>GET /api/v1/features</c> actually returns.</para>
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class FeatureAdoptionParityTests : IAsyncLifetime
{
    private static readonly Uri Features = new("/api/v1/features", UriKind.Relative);

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public FeatureAdoptionParityTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("feature_adoption_parity");
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
    public async Task The_SQL_adoption_count_agrees_with_FeatureResolution_across_the_state_matrix()
    {
        // Two subject features carry the varying state: CRM (off by default) shows an override or a
        // plan turning something ON; POS (on by default) shows a plan turning something OFF. Every
        // other feature is untouched by every gym below, so it resolves at the platform default for
        // all of them — the parity check exercises that agreement too, not only the two subjects.
        const string TurnedOn = PlatformFeatures.Crm;
        const string TurnedOff = PlatformFeatures.Pos;

        var gyms = new List<GymState>();

        // 1. No subscription at all: both subject features fall through to the platform default.
        Guid noSub = await CreateGymAsync("parity-no-subscription");
        gyms.Add(new GymState(noSub));

        // 2. A plan that includes: an explicit opinion turning CRM on.
        Guid includesPlan = await CreatePlanAsync("PARITY_INCL");
        await SetPlanFeatureAsync(includesPlan, TurnedOn, isEnabled: true);
        Guid planIncludes = await CreateGymAsync("parity-plan-includes");
        await AssignAsync(planIncludes, includesPlan);
        gyms.Add(new GymState(planIncludes, planOpinions: new Dictionary<string, bool> { [TurnedOn] = true }));

        // 3. A plan that excludes: an explicit opinion turning POS off.
        Guid excludesPlan = await CreatePlanAsync("PARITY_EXCL");
        await SetPlanFeatureAsync(excludesPlan, TurnedOff, isEnabled: false);
        Guid planExcludes = await CreateGymAsync("parity-plan-excludes");
        await AssignAsync(planExcludes, excludesPlan);
        gyms.Add(new GymState(planExcludes, planOpinions: new Dictionary<string, bool> { [TurnedOff] = false }));

        // 4. A plan that is silent: assigned, but the plan states no opinion on anything — both
        // subject features fall through to the default exactly as if there were no subscription.
        Guid silentPlan = await CreatePlanAsync("PARITY_SILENT");
        Guid planSilent = await CreateGymAsync("parity-plan-silent");
        await AssignAsync(planSilent, silentPlan);
        gyms.Add(new GymState(planSilent));

        // 5. An override, one way: no plan at all, a deliberate exception turns CRM on.
        Guid overrideOn = await CreateGymAsync("parity-override-on");
        await SetGymFeaturesAsync(overrideOn, (TurnedOn, true, "Goodwill trial."));
        gyms.Add(new GymState(overrideOn, overrides: new Dictionary<string, bool> { [TurnedOn] = true }));

        // 5b. An override, the other way: the plan includes CRM, but the gym's own exception turns it
        // back off — proving the override wins over a plan opinion, not merely over silence.
        Guid overrideOff = await CreateGymAsync("parity-override-off");
        await AssignAsync(overrideOff, includesPlan);
        await SetGymFeaturesAsync(overrideOff, (TurnedOn, false, "Gym asked to disable."));
        gyms.Add(new GymState(
            overrideOff,
            planOpinions: new Dictionary<string, bool> { [TurnedOn] = true },
            overrides: new Dictionary<string, bool> { [TurnedOn] = false }));

        // 6. A deleted gym: held an override, then deleted. Deliberately never added to `gyms` below —
        // the production count excludes it entirely, not merely its override, and so must this matrix.
        Guid deletedGym = await CreateGymAsync("parity-deleted");
        await SetGymFeaturesAsync(deletedGym, (TurnedOn, true, "Goodwill trial."));
        await PlatformApiHelpers.DeleteGymAsync(_client, deletedGym, "parity-deleted");

        JsonElement catalogue = await CatalogueAsync();
        Dictionary<string, bool> defaults = catalogue.EnumerateArray()
            .ToDictionary(
                feature => feature.GetProperty("key").GetString()!,
                feature => feature.GetProperty("defaultEnabled").GetBoolean());

        foreach (FeatureDefinition definition in PlatformFeatures.All)
        {
            int expected = gyms.Count(gym => FeatureResolution.Resolve(
                    definition.Key,
                    gym.Overrides.TryGetValue(definition.Key, out bool overrideValue) ? overrideValue : null,
                    gym.PlanOpinions.TryGetValue(definition.Key, out bool planValue) ? planValue : null,
                    defaults[definition.Key])
                .Enabled);

            int actual = FeatureByKey(catalogue, definition.Key).GetProperty("adoptionCount").GetInt32();

            Assert.True(
                expected == actual,
                $"{definition.Key}: FeatureResolution over the {gyms.Count}-gym matrix says {expected}, " +
                $"but GET /features' SQL restatement says {actual}.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One test gym's known inputs to <see cref="FeatureResolution.Resolve"/>: which features it
    /// holds an override on, and which its plan has an opinion on. A key absent from either dictionary
    /// is silence — the same "not present" that <c>Resolve</c> itself treats as <see langword="null"/>.
    /// </summary>
    private sealed class GymState
    {
        public GymState(
            Guid gymId,
            IReadOnlyDictionary<string, bool>? planOpinions = null,
            IReadOnlyDictionary<string, bool>? overrides = null)
        {
            GymId = gymId;
            PlanOpinions = planOpinions ?? new Dictionary<string, bool>();
            Overrides = overrides ?? new Dictionary<string, bool>();
        }

        public Guid GymId { get; }

        public IReadOnlyDictionary<string, bool> PlanOpinions { get; }

        public IReadOnlyDictionary<string, bool> Overrides { get; }
    }

    private Task<Guid> CreateGymAsync(string slug) => PlatformApiHelpers.CreateGymAsync(_client, slug);

    private async Task<Guid> CreatePlanAsync(string code) =>
        (await PlatformApiHelpers.CreatePlanAsync(_client, code)).Id;

    private Task<HttpResponseMessage> AssignAsync(Guid gymId, Guid planId) =>
        PlatformApiHelpers.AssignAsync(_client, gymId, planId);

    private Task SetPlanFeatureAsync(Guid planId, string featureKey, bool isEnabled) =>
        FeatureApiHelpers.SetPlanFeatureAsync(_client, planId, featureKey, isEnabled);

    private Task<JsonElement> SetGymFeaturesAsync(
        Guid gymId, params (string Key, bool IsEnabled, string? Reason)[] features) =>
        FeatureApiHelpers.SetGymFeaturesAsync(_client, gymId, features);

    private async Task<JsonElement> CatalogueAsync()
    {
        using HttpResponseMessage response = await _client.GetAsync(Features);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement FeatureByKey(JsonElement catalogue, string key) =>
        catalogue.EnumerateArray().Single(feature => feature.GetProperty("key").GetString() == key);
}
