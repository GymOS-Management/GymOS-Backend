namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>
/// A read-only use case yielding <typeparamref name="TResponse"/>. Queries never
/// mutate state and never raise domain events (CQRS, AD-3): read-side handlers
/// project directly to DTOs.
/// </summary>
#pragma warning disable CA1040 // Marker interface: the type parameter binds query to response for dispatch.
public interface IQuery<TResponse>
{
}
#pragma warning restore CA1040
