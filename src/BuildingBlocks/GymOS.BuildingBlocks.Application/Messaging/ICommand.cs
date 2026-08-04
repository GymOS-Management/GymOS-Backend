namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>
/// A state-changing use case yielding <typeparamref name="TResponse"/> — by
/// convention a <c>Result</c> or <c>Result&lt;T&gt;</c>, so business failures are
/// values (SharedKernel). Commands are immutable records handled by exactly one
/// <see cref="ICommandHandler{TCommand, TResponse}"/>.
/// </summary>
#pragma warning disable CA1040 // Marker interface: the type parameter binds command to response for dispatch.
public interface ICommand<TResponse>
{
}
#pragma warning restore CA1040
