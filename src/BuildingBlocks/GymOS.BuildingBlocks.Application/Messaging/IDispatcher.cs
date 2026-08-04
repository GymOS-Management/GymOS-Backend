namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>
/// Routes commands and queries to their single handler through the behavior
/// pipeline. The in-house replacement for MediatR (AD-1) — endpoints and modules
/// depend on this interface only.
/// </summary>
public interface IDispatcher
{
    Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default);

    Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);
}
