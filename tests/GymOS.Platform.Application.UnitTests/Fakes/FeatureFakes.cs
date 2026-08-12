using System.Globalization;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Domain.Features;

namespace GymOS.Platform.Application.UnitTests.Fakes;

/// <summary>
/// No-op stand-in for the redundancy sweep. The subscription handler tests are not testing feature
/// withdrawal — they exist to pin subscription behaviour — so this simply records that it was asked
/// and, unless a test configures otherwise, reports nothing to withdraw.
/// </summary>
internal sealed class FakeFeatureExceptionMaintainer : IFeatureExceptionMaintainer
{
    public List<Guid> CalledForGymIds { get; } = [];

    public List<Guid?> CalledWithLivePlanIds { get; } = [];

    public int ReturnValue { get; set; }

    public Task<int> WithdrawRedundantAsync(
        Guid gymId, Guid? livePlanIdAfterChange, CancellationToken cancellationToken)
    {
        CalledForGymIds.Add(gymId);
        CalledWithLivePlanIds.Add(livePlanIdAfterChange);
        return Task.FromResult(ReturnValue);
    }
}

/// <summary>In-memory stand-in for the feature catalogue and override write side.</summary>
internal sealed class FakeFeatureRepository : IFeatureRepository
{
    private readonly List<Feature> _features = [];
    private readonly List<GymFeatureOverride> _overrides = [];

    public IReadOnlyList<Feature> Features => _features;

    public IReadOnlyList<GymFeatureOverride> Overrides => _overrides;

    public Dictionary<Guid, uint> ExpectedVersions { get; } = [];

    /// <summary>Set to make <see cref="ExpectVersion"/> behave as a stale-version write.</summary>
    public uint? ConflictingVersion { get; set; }

    /// <summary>Seeds the declared catalogue, as the startup seeder would.</summary>
    public void SeedDeclaredCatalogue()
    {
        foreach (FeatureDefinition definition in PlatformFeatures.All)
        {
            _features.Add(Feature.FromDefinition(definition));
        }
    }

    public Feature Seed(FeatureDefinition definition)
    {
        Feature feature = Feature.FromDefinition(definition);
        _features.Add(feature);
        return feature;
    }

    public void SeedOverride(GymFeatureOverride gymFeatureOverride) =>
        _overrides.Add(gymFeatureOverride);

    /// <summary>The key of a seeded feature, for tests that assert on projections.</summary>
    public string KeyOf(Guid featureId) =>
        _features.SingleOrDefault(feature => feature.Id == featureId)?.Key ?? string.Empty;

    public Task<Feature?> FindByIdAsync(Guid featureId, CancellationToken cancellationToken) =>
        Task.FromResult(_features.SingleOrDefault(feature => feature.Id == featureId));

    public Task<Feature?> FindByKeyAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_features.SingleOrDefault(feature =>
            string.Equals(feature.Key, key, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<Feature>> ListAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Feature>>(_features);

    public void ExpectVersion(Feature feature, uint expectedVersion)
    {
        ExpectedVersions[feature.Id] = expectedVersion;

        if (ConflictingVersion == expectedVersion)
        {
            throw new GymOS.BuildingBlocks.Application.Abstractions.ConcurrencyConflictException();
        }
    }

    public Task<IReadOnlyList<GymFeatureOverride>> ListOverridesForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<GymFeatureOverride>>(
            _overrides.Where(over => over.GymId == gymId).ToList());

    public void AddOverride(GymFeatureOverride gymFeatureOverride) => _overrides.Add(gymFeatureOverride);

    public void RemoveOverride(GymFeatureOverride gymFeatureOverride) => _overrides.Remove(gymFeatureOverride);
}

/// <summary>
/// Read side, resolving from the same aggregates the write side mutates — including the real
/// <see cref="FeatureResolution"/>, so a handler test exercises the actual three-layer rule rather
/// than a fake's imitation of it.
/// </summary>
internal sealed class FakeFeatureQueries : IFeatureQueries
{
    private readonly FakeFeatureRepository _features;
    private readonly FakePlanRepository _plans;
    private readonly FakeSubscriptionRepository _subscriptions;

    public FakeFeatureQueries(
        FakeFeatureRepository features,
        FakePlanRepository plans,
        FakeSubscriptionRepository subscriptions)
    {
        _features = features;
        _plans = plans;
        _subscriptions = subscriptions;
    }

    public uint RowVersion { get; set; } = 1;

    public Task<IReadOnlyList<FeatureListItem>> ListCatalogueAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeatureListItem>>(
            _features.Features
                .OrderBy(feature => feature.Category, StringComparer.Ordinal)
                .ThenBy(feature => feature.Name, StringComparer.Ordinal)
                .Select(Project)
                .ToList());

    public Task<FeatureListItem?> GetAsync(Guid featureId, CancellationToken cancellationToken)
    {
        Feature? feature = _features.Features.SingleOrDefault(candidate => candidate.Id == featureId);
        return Task.FromResult(feature is null ? null : ProjectWithoutCounts(feature));
    }

    public Task<IReadOnlyList<ResolvedFeatureDetail>> ResolveForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        Guid? planId = _subscriptions.Subscriptions
            .SingleOrDefault(subscription => subscription.GymId == gymId && subscription.IsLive)
            ?.PlanId;

        var resolved = _features.Features
            .OrderBy(feature => feature.Category, StringComparer.Ordinal)
            .ThenBy(feature => feature.Name, StringComparer.Ordinal)
            .Select(feature =>
            {
                GymFeatureOverride? over = _features.Overrides.SingleOrDefault(candidate =>
                    candidate.GymId == gymId && candidate.FeatureId == feature.Id);

                bool? planEnabled = planId is null
                    ? null
                    : _plans.Plans
                        .SingleOrDefault(plan => plan.Id == planId)
                        ?.Features
                        .SingleOrDefault(planFeature => planFeature.FeatureId == feature.Id)
                        ?.IsEnabled;

                ResolvedFeature answer = FeatureResolution.Resolve(
                    feature.Key, over?.IsEnabled, planEnabled, feature.DefaultEnabled);

                bool decidedByOverride = answer.Source == FeatureSource.Override;

                return new ResolvedFeatureDetail(
                    feature.Id,
                    feature.Key,
                    feature.Name,
                    feature.Category,
                    answer.Enabled,
                    answer.Source.ToString(),
                    feature.IsPremium,
                    decidedByOverride ? over?.Reason : null,
                    decidedByOverride ? over?.SetAtUtc : null,

                    // Not resolved here: this fake has no user directory to join against, and the
                    // email round trip is exactly what GymFeatureApiTests.SetByEmailAsync (and the
                    // integration-only An_override_carries_who_set_it_and_when) proves against
                    // real PostgreSQL instead.
                    null);
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ResolvedFeatureDetail>>(resolved);
    }

    private FeatureListItem Project(Feature feature) => new(
        feature.Id,
        feature.Key,
        feature.Name,
        feature.Description,
        feature.Category,
        feature.DefaultEnabled,
        feature.IsPremium,

        // Adoption is a set-level count the real query computes in SQL; the fake reports the
        // override tally only, and the tests that care about adoption are the integration ones.
        AdoptionCount: 0,
        OverrideCount: _features.Overrides.Count(over => over.FeatureId == feature.Id),
        RowVersion.ToString(CultureInfo.InvariantCulture),
        IsDeclared: PlatformFeatures.All.Any(definition =>
            string.Equals(definition.Key, feature.Key, StringComparison.Ordinal)));

    /// <summary>
    /// Mirrors production's split: <see cref="GetAsync"/> backs <c>PUT /features/{id}</c>'s
    /// read-back and uses <c>FeatureQueries.ProjectWithoutCounts</c> there, returning <c>null</c>
    /// for both counts rather than paying for (or faking) the adoption/override scan. Only
    /// <see cref="ListCatalogueAsync"/> reports real counts, via <see cref="Project"/> above — a
    /// fake that shared one projection between the two taught the wrong contract, the same
    /// missing-<c>Include</c> shape the <c>PlanRepository</c> trap already cost this codebase once.
    /// </summary>
    private FeatureListItem ProjectWithoutCounts(Feature feature) => new(
        feature.Id,
        feature.Key,
        feature.Name,
        feature.Description,
        feature.Category,
        feature.DefaultEnabled,
        feature.IsPremium,
        AdoptionCount: null,
        OverrideCount: null,
        RowVersion.ToString(CultureInfo.InvariantCulture),
        IsDeclared: PlatformFeatures.All.Any(definition =>
            string.Equals(definition.Key, feature.Key, StringComparison.Ordinal)));
}
