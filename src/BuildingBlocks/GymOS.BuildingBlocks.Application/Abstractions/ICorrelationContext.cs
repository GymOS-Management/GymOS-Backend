namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The correlation spine (AD-38): one id per originating request or trigger, flowing
/// through commands, outbox messages, jobs, and integration calls so a single id
/// reconstructs the whole causal chain. Set at the edge (HTTP middleware now; job
/// runner and outbox processor when they land); <see langword="null"/> only when no
/// edge has run — e.g. design-time tooling.
/// </summary>
public interface ICorrelationContext
{
    Guid? CorrelationId { get; }
}
