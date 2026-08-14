namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The tenant this scope is acting for, or platform scope when there is none.
/// </summary>
/// <remarks>
/// <para>Deliberately separate from <see cref="ICurrentUser"/>. Who the caller is and which tenant
/// they are acting for are different facts, and a platform administrator operating on one gym has
/// both at once. Collapsing them into one object creates the natural home for the line that ends
/// this control — <c>if (user.IsPlatformAdmin) skipFilter()</c> — so they stay apart.</para>
/// <para>Platform scope is not "every tenant". A platform-scope request reads no tenant-scoped rows
/// at all; to read a tenant's data you name that tenant.</para>
/// </remarks>
public interface ITenantContext
{
    Guid? TenantId { get; }

    bool IsPlatformScope { get; }
}
