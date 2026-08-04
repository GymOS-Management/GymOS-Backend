namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>Handles exactly one command type. Registered scoped by assembly scanning.</summary>
public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken cancellationToken);
}
