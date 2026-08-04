using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.BuildingBlocks.Infrastructure.Context;

/// <summary>
/// The caller for work no human initiated: startup seeding, background jobs, outbox
/// delivery. Everything is <see langword="null"/>, which is the honest answer — audit and
/// outbox rows then record a system action rather than attributing it to whoever happened
/// to trigger the process.
/// </summary>
public sealed class SystemUser : ICurrentUser
{
    public Guid? UserId => null;

    public string? Email => null;

    public bool IsAuthenticated => false;

    public string? IpAddress => null;

    public string? UserAgent => null;
}
