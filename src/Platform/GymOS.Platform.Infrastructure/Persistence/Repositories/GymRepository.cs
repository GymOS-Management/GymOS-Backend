using GymOS.BuildingBlocks.Infrastructure.Persistence;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Persistence.Repositories;

/// <summary>EF Core adapter for the <see cref="Gym"/> aggregate.</summary>
internal sealed class GymRepository : IGymRepository
{
    private readonly AppDbContext _dbContext;

    public GymRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<Gym?> FindByIdAsync(Guid gymId, CancellationToken cancellationToken) =>
        _dbContext.Set<Gym>().SingleOrDefaultAsync(gym => gym.Id == gymId, cancellationToken);

    public Task<Gym?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
    {
        // Lower-cased here as well as by the domain rule, because this lookup decides whether a slug is
        // free: comparing "Downtown" against a stored "downtown" would report free and then hit the
        // unique index as a 500.
        string normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();

        return _dbContext.Set<Gym>().SingleOrDefaultAsync(gym => gym.Slug == normalized, cancellationToken);
    }

    public Task<GymBranding?> FindBrandingAsync(Guid gymId, CancellationToken cancellationToken) =>
        _dbContext.Set<GymBranding>()
            .SingleOrDefaultAsync(branding => branding.GymId == gymId, cancellationToken);

    public Task<bool> CustomDomainTakenAsync(
        string customDomain,
        Guid exceptGymId,
        CancellationToken cancellationToken)
    {
        string normalized = (customDomain ?? string.Empty).Trim().ToLowerInvariant();

        return _dbContext.Set<GymBranding>()
            .AnyAsync(
                branding => branding.CustomDomain == normalized && branding.GymId != exceptGymId,
                cancellationToken);
    }

    public void Add(Gym gym) => _dbContext.Set<Gym>().Add(gym);

    public void AddBranding(GymBranding branding) => _dbContext.Set<GymBranding>().Add(branding);

    /// <summary>
    /// Overwrites the loaded row version with the caller's, so EF puts *their* expectation in the
    /// UPDATE's WHERE clause. If the row has moved on, the statement matches nothing and EF reports a
    /// concurrency failure — the same mechanism as <c>UserRepository</c>.
    /// </summary>
    public void ExpectVersion(Gym gym, ulong expectedVersion) =>
        _dbContext.Entry(gym).Property<ulong>(ConcurrencyConventions.RowVersionProperty).OriginalValue = expectedVersion;

    /// <summary>
    /// Flags the audit timestamp as modified, which is enough for EF to issue an UPDATE against the gyms
    /// row — and SQL Server advances <c>row_version</c> on any update.
    /// </summary>
    public void MarkAggregateChanged(Gym gym) =>
        _dbContext.Entry(gym).Property(entity => entity.UpdatedAtUtc).IsModified = true;
}
