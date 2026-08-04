using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.TestingSupport;

/// <summary>
/// Deterministic <see cref="IClock"/> for tests (AD-40): time moves only when the
/// test says so, making expiry, scheduling, and DST logic exactly testable.
/// </summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _utcNow;

    public FakeClock(DateTimeOffset initialUtcNow) => _utcNow = initialUtcNow;

    public DateTimeOffset UtcNow => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);

    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
}
