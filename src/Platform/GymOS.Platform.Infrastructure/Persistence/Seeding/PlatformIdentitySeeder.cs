using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// Declare-and-validate startup sync (AD-22): reconciles the permission catalog and
/// system-role grants to exactly what the code declares. Idempotent — safe on every
/// startup of every instance. Orphaned permissions (rows whose key no module
/// declares anymore) are logged loudly, never silently deleted: revoking real
/// grants is an explicit migration decision.
/// </summary>
public sealed partial class PlatformIdentitySeeder
{
    /// <summary>
    /// Same lock name as <see cref="FeatureCatalogSeeder"/> — deliberately: both are startup-scope
    /// syncs against the same fresh database (Program.cs runs this one first), and a single lock
    /// spanning both is what stops two instances racing each other's inserts, whichever seeder
    /// either one happens to be running.
    /// </summary>
    private const string CatalogSeedLock = "platform.catalog-seed";

    private readonly AppDbContext _dbContext;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAdvisoryLock _advisoryLock;
    private readonly ILogger<PlatformIdentitySeeder> _logger;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Identity catalog sync skipped: the database has pending migrations. " +
        "Apply migrations (Database:MigrateOnStartup or dotnet ef database update) and restart.")]
    private static partial void LogSyncSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Permission '{PermissionKey}' exists in the database but no module declares it. " +
        "Remove it with an explicit migration once grants are reviewed.")]
    private static partial void LogOrphanedPermission(ILogger logger, string permissionKey);

    public PlatformIdentitySeeder(
        AppDbContext dbContext,
        IUnitOfWork unitOfWork,
        IAdvisoryLock advisoryLock,
        ILogger<PlatformIdentitySeeder> logger)
    {
        _dbContext = dbContext;
        _unitOfWork = unitOfWork;
        _advisoryLock = advisoryLock;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        // Checked before the transaction opens, same as FeatureCatalogSeeder: a database with
        // pending migrations gets no lock and no transaction.
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            LogSyncSkippedPendingMigrations(_logger);
            return;
        }

        // Two instances booting against a fresh database both see no permissions declared and both
        // try to insert the whole catalogue — one of them loses to the permission key's unique
        // index. The lock is held for the life of this transaction, so the instance that waits
        // re-reads the existing permissions and roles below only after the other has committed.
        // Saving through IUnitOfWork rather than the context directly means a race lost anyway
        // surfaces as UniquenessConflictException rather than a raw DbUpdateException escaping
        // SeedAsync and killing the host via Program.cs's catch-all.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);
        await _advisoryLock.AcquireAsync(CatalogSeedLock, cancellationToken);

        Dictionary<string, Permission> permissionsByKey = await SyncPermissionsAsync(cancellationToken);
        await SyncSystemRolesAsync(permissionsByKey, cancellationToken);

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Dictionary<string, Permission>> SyncPermissionsAsync(CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Set<Permission>().ToDictionaryAsync(p => p.Key, cancellationToken);

        foreach (PermissionDefinition definition in PlatformPermissions.All)
        {
            if (existing.TryGetValue(definition.Key, out Permission? permission))
            {
                if (permission.Module != definition.Module || permission.Description != definition.Description)
                {
                    permission.UpdateMetadata(definition.Module, definition.Description);
                }
            }
            else
            {
                permission = Permission.Create(definition.Key, definition.Module, definition.Description);
                _dbContext.Add(permission);
                existing.Add(definition.Key, permission);
            }
        }

        var declaredKeys = PlatformPermissions.All.Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string orphanKey in existing.Keys.Where(key => !declaredKeys.Contains(key)))
        {
            LogOrphanedPermission(_logger, orphanKey);
        }

        return existing;
    }

    private async Task SyncSystemRolesAsync(
        Dictionary<string, Permission> permissionsByKey,
        CancellationToken cancellationToken)
    {
        var existingRoles = await _dbContext.Set<Role>()
            .Include(role => role.Permissions)
            .Where(role => role.Scope == RoleScope.Platform && role.IsSystem)
            .ToDictionaryAsync(role => role.NormalizedName, cancellationToken);

        foreach (SystemRoleDefinition definition in SystemRoles.Definitions)
        {
            string normalizedName = Role.Normalize(definition.Name);
            if (!existingRoles.TryGetValue(normalizedName, out Role? role))
            {
                role = Role.Create(definition.Name, definition.Description, RoleScope.Platform, isSystem: true);
                _dbContext.Add(role);
                existingRoles.Add(normalizedName, role);
            }

            IReadOnlyCollection<Guid> targetPermissionIds = definition.GrantsAllPermissions
                ? permissionsByKey.Values.Select(permission => permission.Id).ToList()
                : definition.Permissions.Select(key => permissionsByKey[key].Id).ToList();

            foreach (Guid permissionId in targetPermissionIds)
            {
                role.GrantPermission(permissionId);
            }

            // System-role grants are code-canonical: revoke anything not declared.
            foreach (Guid grantedId in role.Permissions.Select(grant => grant.PermissionId).ToList())
            {
                if (!targetPermissionIds.Contains(grantedId))
                {
                    role.RevokePermission(grantedId);
                }
            }
        }
    }
}
