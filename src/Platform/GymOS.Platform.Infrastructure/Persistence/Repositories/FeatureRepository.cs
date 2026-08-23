using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Domain.Features;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>EF Core adapter for the feature catalogue and per-gym overrides.</summary>
internal sealed class FeatureRepository : IFeatureRepository
{
    private readonly AppDbContext _dbContext;

    public FeatureRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<Feature?> FindByIdAsync(Guid featureId, CancellationToken cancellationToken) =>
        _dbContext.Set<Feature>()
            .SingleOrDefaultAsync(feature => feature.Id == featureId, cancellationToken);

    /// <summary>Hits the unique index on <c>key</c> — one row or none.</summary>
    public Task<Feature?> FindByKeyAsync(string key, CancellationToken cancellationToken)
    {
        string normalized = (key ?? string.Empty).Trim().ToLowerInvariant();

        return _dbContext.Set<Feature>()
            .SingleOrDefaultAsync(feature => feature.Key == normalized, cancellationToken);
    }

    public async Task<IReadOnlyList<Feature>> ListAllAsync(CancellationToken cancellationToken) =>
        await _dbContext.Set<Feature>()
            .OrderBy(feature => feature.Key)
            .ToListAsync(cancellationToken);

    public void ExpectVersion(Feature feature, ulong expectedVersion) =>
        _dbContext.Entry(feature).Property<ulong>(ConcurrencyConventions.RowVersionProperty).OriginalValue = expectedVersion;

    public async Task<IReadOnlyList<GymFeatureOverride>> ListOverridesForGymAsync(
        Guid gymId,
        CancellationToken cancellationToken) =>
        await _dbContext.Set<GymFeatureOverride>()
            .Where(over => over.GymId == gymId)
            .ToListAsync(cancellationToken);

    public void AddOverride(GymFeatureOverride gymFeatureOverride) =>
        _dbContext.Set<GymFeatureOverride>().Add(gymFeatureOverride);

    public void RemoveOverride(GymFeatureOverride gymFeatureOverride) =>
        _dbContext.Set<GymFeatureOverride>().Remove(gymFeatureOverride);
}
