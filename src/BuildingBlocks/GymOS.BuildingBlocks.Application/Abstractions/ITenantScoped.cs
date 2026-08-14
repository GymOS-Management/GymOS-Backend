namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Marks an entity whose rows belong to one tenant, and which must therefore carry a
/// <c>tenant_id</c> column protected by a row-level security policy (R14).
/// </summary>
/// <remarks>
/// A marker interface rather than an EF annotation so an architecture test can see it too: an entity
/// in a future tenant module that forgets this fails a test, instead of silently opting out of
/// isolation and passing every other gate. <c>TenantIsolationValidator</c> keys on it at startup.
/// </remarks>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
