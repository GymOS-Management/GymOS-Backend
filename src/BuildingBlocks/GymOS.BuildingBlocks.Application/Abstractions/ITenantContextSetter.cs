namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The write half of <see cref="ITenantContext"/>, for the two places allowed to name the tenant.
/// </summary>
/// <remarks>
/// Reading the acting tenant is something any handler may do; setting it belongs to the request
/// middleware and to a job iterating gyms, and nowhere else. Separating the capabilities means a
/// handler cannot re-point its own scope even by accident, and the write sites stay greppable.
/// </remarks>
public interface ITenantContextSetter
{
    void SetTenant(Guid tenantId);
}
