using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using RowVersion = GymOS.BuildingBlocks.Application.Abstractions.RowVersion;

namespace GymOS.Platform.Infrastructure.Persistence.Queries;

internal sealed class FeatureQueries : IFeatureQueries
{
    private static readonly SubscriptionStatus[] LiveStatuses = SubscriptionStatusTransitions.Live;

    /// <summary>
    /// The code-declared keys, computed once from the constant catalogue. Compared in memory in
    /// <see cref="Map"/>, never pushed into the query below: the declared set already lives in
    /// <see cref="PlatformFeatures"/>, and a second copy of it in SQL is exactly the kind of drift
    /// this increment removes elsewhere.
    /// </summary>
    private static readonly HashSet<string> DeclaredKeys = PlatformFeatures.All
        .Select(definition => definition.Key)
        .ToHashSet(StringComparer.Ordinal);

    private readonly AppDbContext _dbContext;

    public FeatureQueries(AppDbContext dbContext) => _dbContext = dbContext;

    public async Task<IReadOnlyList<FeatureListItem>> ListCatalogueAsync(CancellationToken cancellationToken)
    {
        var rows = await Project()
            .OrderBy(row => row.Category)
            .ThenBy(row => row.Name)
            .ToListAsync(cancellationToken);

        return rows.Select(Map).ToList();
    }

    public async Task<FeatureListItem?> GetAsync(Guid featureId, CancellationToken cancellationToken)
    {
        var row = await Project()
            .SingleOrDefaultAsync(candidate => candidate.Id == featureId, cancellationToken);

        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<ResolvedFeatureDetail>> ResolveForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        // The gym's live subscription decides which plan's opinions count. Null when it has none —
        // and then the plan layer is silent for every feature, which resolves to the platform
        // default (the approved decision: a provisioning gym stays configurable).
        Guid? planId = await _dbContext.Set<GymSubscription>()
            .AsNoTracking()
            .Where(subscription => subscription.GymId == gymId
                && LiveStatuses.Contains(subscription.Status))
            .Select(subscription => (Guid?)subscription.PlanId)
            .FirstOrDefaultAsync(cancellationToken);

        // One round trip for the whole catalogue, with each feature's two possible opinions beside
        // it. Resolution itself is NOT done in SQL: FeatureResolution is the single definition of
        // the rule, exhaustively tested, and duplicating it in a query expression is exactly how the
        // two would drift.
        var rows = await _dbContext.Set<Feature>()
            .AsNoTracking()
            .Select(feature => new
            {
                feature.Id,
                feature.Key,
                feature.Name,
                feature.Category,
                feature.DefaultEnabled,
                feature.IsPremium,
                PlanEnabled = _dbContext.Set<PlanFeature>()
                    .Where(row => row.FeatureId == feature.Id && row.PlanId == planId)
                    .Select(row => (bool?)row.IsEnabled)
                    .FirstOrDefault(),
                // Joined to the user table for the email rather than returning the id: provenance is
                // only worth carrying if the caller can read it back as a person, and
                // GymFeatureApiTests already proves this exact join in SQL. A left join, not an
                // inner one — SetByUserId is nullable and cleared when that account is deleted, so
                // the override (and its history) must survive losing its author.
                Override = _dbContext.Set<GymFeatureOverride>()
                    .Where(row => row.FeatureId == feature.Id && row.GymId == gymId)
                    .Select(row => new
                    {
                        Enabled = (bool?)row.IsEnabled,
                        row.Reason,
                        row.SetAtUtc,
                        SetByEmail = _dbContext.Set<User>()
                            .Where(user => user.Id == row.SetByUserId)
                            .Select(user => (string?)user.Email)
                            .FirstOrDefault(),
                    })
                    .FirstOrDefault(),
            })
            .OrderBy(row => row.Category)
            .ThenBy(row => row.Name)
            .ToListAsync(cancellationToken);

        return rows
            .Select(row =>
            {
                ResolvedFeature resolved = FeatureResolution.Resolve(
                    row.Key, row.Override?.Enabled, row.PlanEnabled, row.DefaultEnabled);

                // Only meaningful when the override decided it — a stale reason, timestamp, or author
                // beside a plan-sourced answer would read as though an exception were in force.
                bool decidedByOverride = resolved.Source == FeatureSource.Override;

                return new ResolvedFeatureDetail(
                    row.Id,
                    row.Key,
                    row.Name,
                    row.Category,
                    resolved.Enabled,
                    resolved.Source.ToString(),
                    row.IsPremium,
                    decidedByOverride ? row.Override?.Reason : null,
                    decidedByOverride ? row.Override?.SetAtUtc : null,
                    decidedByOverride ? row.Override?.SetByEmail : null);
            })
            .ToList();
    }

    /// <summary>
    /// The catalogue projection, including the adoption count.
    ///
    /// <para><b>Adoption counts gyms that resolve the feature ON</b>, through the whole three-layer
    /// rule — not plan rows, and not overrides. Counting one layer would understate reality and
    /// mislead the decision the number exists to inform ("is anyone actually using this?"). Deleted
    /// gyms are excluded from <b>both</b> counts: they are not using anything, and an exception
    /// granted to a tenant that no longer exists is not a live exception.</para>
    /// </summary>
    private IQueryable<FeatureRow> Project() =>
        _dbContext.Set<Feature>()
            .AsNoTracking()
            .Select(feature => new FeatureRow
            {
                Id = feature.Id,
                Key = feature.Key,
                Name = feature.Name,
                Description = feature.Description,
                Category = feature.Category,
                DefaultEnabled = feature.DefaultEnabled,
                IsPremium = feature.IsPremium,
                RowVersion = EF.Property<uint>(feature, "xmin"),

                // Counted from the same gym population as adoption below, and written the same way
                // so that the shared `Status != Deleted` filter is visible rather than remembered:
                // the two numbers sit side by side in one row, and one of them silently including
                // dead tenants made "2 of 5 gyms hold an exception" a comparison of different sets.
                OverrideCount = _dbContext.Set<Gym>()
                    .Where(gym => gym.Status != GymStatus.Deleted)
                    .Count(gym => _dbContext.Set<GymFeatureOverride>()
                        .Any(over => over.GymId == gym.Id && over.FeatureId == feature.Id)),

                // Written as the same fallback chain FeatureResolution.Resolve applies —
                // override ?? plan ?? default — so the SQL and the domain rule can be read side by
                // side and seen to agree. (An earlier version spelled this out as nested EXISTS
                // clauses; it was correct-looking and unverifiable, which is worse than either.)
                AdoptionCount = _dbContext.Set<Gym>()
                    .Where(gym => gym.Status != GymStatus.Deleted)
                    .Count(gym =>
                        (_dbContext.Set<GymFeatureOverride>()
                            .Where(over => over.GymId == gym.Id && over.FeatureId == feature.Id)
                            .Select(over => (bool?)over.IsEnabled)
                            .FirstOrDefault()
                        ?? _dbContext.Set<GymSubscription>()
                            .Where(subscription => subscription.GymId == gym.Id
                                && LiveStatuses.Contains(subscription.Status))
                            .SelectMany(subscription => _dbContext.Set<PlanFeature>()
                                .Where(planFeature => planFeature.PlanId == subscription.PlanId
                                    && planFeature.FeatureId == feature.Id)
                                .Select(planFeature => (bool?)planFeature.IsEnabled))
                            .FirstOrDefault()
                        ?? feature.DefaultEnabled) == true),
            });

    private static FeatureListItem Map(FeatureRow row) => new(
        row.Id,
        row.Key,
        row.Name,
        row.Description,
        row.Category,
        row.DefaultEnabled,
        row.IsPremium,
        row.AdoptionCount,
        row.OverrideCount,
        RowVersion.ToToken(row.RowVersion),

        // In memory against the code constant, not in SQL — see DeclaredKeys.
        DeclaredKeys.Contains(row.Key));

    /// <summary>
    /// Named rather than anonymous so <see cref="ListCatalogueAsync"/> and <see cref="GetAsync"/>
    /// share one projection — two copies of the adoption expression is two chances to disagree.
    /// </summary>
    private sealed class FeatureRow
    {
        public Guid Id { get; init; }

        public string Key { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public string Description { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public bool DefaultEnabled { get; init; }

        public bool IsPremium { get; init; }

        public int AdoptionCount { get; init; }

        public int OverrideCount { get; init; }

        public uint RowVersion { get; init; }
    }
}
