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
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PlatformIdentitySeeder> _logger;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Identity catalog sync skipped: the database has pending migrations. " +
        "Apply migrations (Database:MigrateOnStartup or dotnet ef database update) and restart.")]
    private static partial void LogSyncSkippedPendingMigrations(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Permission '{PermissionKey}' exists in the database but no module declares it. " +
        "Remove it with an explicit migration once grants are reviewed.")]
    private static partial void LogOrphanedPermission(ILogger logger, string permissionKey);

    public PlatformIdentitySeeder(AppDbContext dbContext, ILogger<PlatformIdentitySeeder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            LogSyncSkippedPendingMigrations(_logger);
            return;
        }

        Dictionary<string, Permission> permissionsByKey = await SyncPermissionsAsync(cancellationToken);
        await SyncSystemRolesAsync(permissionsByKey, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
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
