namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>Handles exactly one query type. Registered scoped by assembly scanning.</summary>
public interface IQueryHandler<in TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    Task<TResponse> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
