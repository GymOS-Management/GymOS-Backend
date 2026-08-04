using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.BuildingBlocks.Infrastructure.Time;

/// <summary>
/// The single production source of wall-clock time (AD-40). This is the only file
/// in the codebase permitted to read the system clock — everywhere else the banned-API
/// analyzer turns direct clock reads into build errors.
/// </summary>
public sealed class SystemClock : IClock
{
#pragma warning disable RS0030 // Banned API: this adapter is the sole sanctioned call site (AD-40).
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
#pragma warning restore RS0030
}
