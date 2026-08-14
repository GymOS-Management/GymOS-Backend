using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>Scoped, set once per request (or per job iteration), read many times after.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextSetter
{
    public Guid? TenantId { get; private set; }

    public bool IsPlatformScope => TenantId is null;

    /// <summary>
    /// Names the tenant this scope acts for.
    /// </summary>
    /// <remarks>
    /// Re-pointing a scope at a different tenant is refused. One request acts for one tenant, and a
    /// second call would mean any query that already ran did so under the previous value — a
    /// difference the caller cannot see and the database will not report.
    /// </remarks>
    public void SetTenant(Guid tenantId)
    {
        if (TenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                $"This scope already acts for tenant {existing} and cannot be re-pointed at {tenantId}.");
        }

        TenantId = tenantId;
    }
}
