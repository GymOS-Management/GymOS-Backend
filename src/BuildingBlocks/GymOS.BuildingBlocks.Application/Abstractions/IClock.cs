namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The only sanctioned source of "now" (AD-40). Direct clock reads
/// (<c>DateTime.Now/UtcNow</c>, <c>DateTimeOffset.Now/UtcNow</c>) are banned by
/// analyzer everywhere except the production <c>SystemClock</c> adapter.
/// Read the clock at the edges (request start, job start) and pass time inward
/// as data — business logic computing from context timestamps stays replayable.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
