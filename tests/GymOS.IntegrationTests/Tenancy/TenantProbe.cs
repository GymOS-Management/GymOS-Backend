using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.IntegrationTests.Tenancy;

/// <summary>
/// A tenant-scoped row that exists only for the isolation tests.
/// </summary>
/// <remarks>
/// R14's mechanism is built before the first real tenant-scoped table (Members) so that isolation is
/// never retrofitted onto populated data. This is the subject it is proven against: minimal on
/// purpose, because the properties under test — a policy filters, an owner does not bypass, an unset
/// tenant reads nothing — are properties of the mechanism, not of any domain.
/// </remarks>
public sealed class TenantProbe : ITenantScoped
{
    public Guid Id { get; init; }

    public Guid TenantId { get; init; }

    public string Note { get; init; } = string.Empty;
}
