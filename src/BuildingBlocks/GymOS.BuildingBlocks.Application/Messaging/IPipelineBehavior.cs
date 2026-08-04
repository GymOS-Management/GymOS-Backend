namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>Continues the pipeline: the next behavior, or the handler itself.</summary>
public delegate Task<TResponse> PipelineNext<TResponse>();

/// <summary>
/// Cross-cutting stage wrapped around every dispatched command/query. The approved
/// pipeline order (AD-39): Validation → Authorization → Idempotency → Audit →
/// Transaction — each stage lands with its milestone and is composed in
/// registration order (first registered runs outermost).
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : notnull
{
    Task<TResponse> HandleAsync(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken);
}
