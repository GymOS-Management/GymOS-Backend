using GymOS.BuildingBlocks.Application.Abstractions;

namespace GymOS.BuildingBlocks.Infrastructure.Context;

/// <summary>
/// Scoped holder behind <see cref="ICorrelationContext"/>. Exactly one edge per scope
/// sets it (HTTP middleware, job runner, outbox processor); everything in the scope
/// reads it. Never a static or AsyncLocal — scope ownership keeps it testable and
/// free of ambient-state surprises (rule R1 in spirit).
/// </summary>
public sealed class CorrelationContext : ICorrelationContext
{
    public Guid? CorrelationId { get; private set; }

    /// <summary>Called once per scope by the owning edge.</summary>
    public void Set(Guid correlationId)
    {
        if (CorrelationId is not null)
        {
            throw new InvalidOperationException(
                "The correlation id for this scope is already set — exactly one edge owns it.");
        }

        CorrelationId = correlationId;
    }
}
