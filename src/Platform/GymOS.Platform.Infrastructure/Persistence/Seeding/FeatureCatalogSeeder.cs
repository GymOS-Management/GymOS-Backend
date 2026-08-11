using GymOS.Platform.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// Startup sync of the feature catalogue (§2.4, §3.5) — <b>insert-only</b>.
///
/// <para>This is the one catalogue that differs from the permission catalogue, and the difference is
/// the whole point of the class. Permission rows are entirely code-owned, so
/// <c>PlatformIdentitySeeder</c> reconciles their metadata on every boot. A feature's
/// <c>name</c>, <c>description</c>, <c>category</c>, <c>default_enabled</c> and <c>is_premium</c> are
/// <b>editable at runtime</b> (§3.5's <c>PUT /features/{id}</c>): what the platform ships enabled is
/// a commercial decision that must not need a deployment. So the declaration supplies the
/// <em>opening</em> values for a key nobody has seen before, and after that the operator owns the
/// row. A seeder that "reconciled" here would silently revert a deliberate change on the next
/// restart — the worst kind of bug, because the operator's edit works until someone reboots.</para>
///
/// <para>Keys themselves stay code-canonical: nothing here (and no endpoint) creates a key that is
/// not declared, and a row whose key no longer appears in code is logged rather than deleted —
/// dropping it would cascade-delete the plan contents and gym overrides referencing it.</para>
/// </summary>
public sealed partial class FeatureCatalogSeeder
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<FeatureCatalogSeeder> _logger;

    [LoggerMessage(Level = LogLevel.Information, Message =
        "Feature catalogue synced: {InsertedCount} new feature(s) inserted, {ExistingCount} left " +
        "untouched (their defaults and copy are operator-owned).")]
    private static partial void LogSynced(ILogger logger, int insertedCount, int existingCount);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Feature catalogue sync skipped: the database has pending migrations. Apply migrations " +
        "(Database:MigrateOnStartup or dotnet ef database update) and restart.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Feature '{FeatureKey}' exists in the database but no module declares it. Plans and gym " +
        "overrides may still reference it, so it is left in place — remove it with an explicit " +
        "migration once those references are reviewed.")]
    private static partial void LogOrphanedFeature(ILogger logger, string featureKey);

    public FeatureCatalogSeeder(AppDbContext dbContext, ILogger<FeatureCatalogSeeder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            LogSkippedPendingMigrations(_logger);
            return;
        }

        HashSet<string> existingKeys = (await _dbContext.Set<Feature>()
                .Select(feature => feature.Key)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        int inserted = 0;
        foreach (FeatureDefinition definition in PlatformFeatures.All)
        {
            if (existingKeys.Contains(definition.Key))
            {
                // Deliberately nothing: the row is the operator's now. See the type's remarks.
                continue;
            }

            _dbContext.Add(Feature.FromDefinition(definition));
            inserted++;
        }

        var declaredKeys = PlatformFeatures.All
            .Select(definition => definition.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string orphan in existingKeys.Where(key => !declaredKeys.Contains(key)))
        {
            LogOrphanedFeature(_logger, orphan);
        }

        if (inserted > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        LogSynced(_logger, inserted, existingKeys.Count);
    }
}
