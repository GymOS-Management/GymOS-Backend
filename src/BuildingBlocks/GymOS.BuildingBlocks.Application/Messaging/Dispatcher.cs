using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace GymOS.BuildingBlocks.Application.Messaging;

/// <summary>
/// Scoped dispatcher implementation. Reflection happens once per (request, response)
/// pair: a typed invoker is built and cached in the singleton
/// <see cref="DispatcherInvokerCache"/>; every subsequent dispatch is cache-hit +
/// generic virtual call. The dispatcher itself is scoped so handlers and behaviors
/// resolve from the current request scope.
/// </summary>
internal sealed class Dispatcher : IDispatcher
{
    private readonly IServiceProvider _services;
    private readonly DispatcherInvokerCache _cache;

    public Dispatcher(IServiceProvider services, DispatcherInvokerCache cache)
    {
        _services = services;
        _cache = cache;
    }

    public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = (RequestInvoker<TResponse>)_cache.GetOrAdd(command.GetType(), typeof(TResponse), isCommand: true);
        return DispatchTracedAsync(command, "command", invoker, cancellationToken);
    }

    public Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var invoker = (RequestInvoker<TResponse>)_cache.GetOrAdd(query.GetType(), typeof(TResponse), isCommand: false);
        return DispatchTracedAsync(query, "query", invoker, cancellationToken);
    }

    private async Task<TResponse> DispatchTracedAsync<TResponse>(
        object request,
        string kind,
        RequestInvoker<TResponse> invoker,
        CancellationToken cancellationToken)
    {
        using Activity? activity = MessagingDiagnostics.ActivitySource.StartActivity(request.GetType().Name);
        activity?.SetTag("gymos.message.kind", kind);
        activity?.SetTag("gymos.message.type", request.GetType().FullName);

        try
        {
            return await invoker.InvokeAsync(request, _services, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }
}

/// <summary>
/// Singleton holding the typed invokers. Instance state on a DI singleton — never a
/// static field (rule R1: no static mutable state).
/// </summary>
internal sealed class DispatcherInvokerCache
{
    private readonly ConcurrentDictionary<(Type Request, Type Response, bool IsCommand), object> _invokers = new();

    public object GetOrAdd(Type requestType, Type responseType, bool isCommand) =>
        _invokers.GetOrAdd((requestType, responseType, isCommand), static key =>
        {
            Type definition = key.IsCommand ? typeof(CommandInvoker<,>) : typeof(QueryInvoker<,>);
            return Activator.CreateInstance(definition.MakeGenericType(key.Request, key.Response))!;
        });
}

internal abstract class RequestInvoker<TResponse>
{
    public abstract Task<TResponse> InvokeAsync(object request, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class CommandInvoker<TCommand, TResponse> : RequestInvoker<TResponse>
    where TCommand : class, ICommand<TResponse>
{
    public override Task<TResponse> InvokeAsync(object request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var command = (TCommand)request;
        ICommandHandler<TCommand, TResponse> handler =
            services.GetService<ICommandHandler<TCommand, TResponse>>()
            ?? throw new InvalidOperationException(
                $"No handler registered for command '{typeof(TCommand).Name}'. " +
                $"Expected an ICommandHandler<{typeof(TCommand).Name}, {typeof(TResponse).Name}> " +
                "registered via AddHandlersFromAssembly.");

        return BehaviorPipeline.ExecuteAsync(command, handler.HandleAsync, services, cancellationToken);
    }
}

internal sealed class QueryInvoker<TQuery, TResponse> : RequestInvoker<TResponse>
    where TQuery : class, IQuery<TResponse>
{
    public override Task<TResponse> InvokeAsync(object request, IServiceProvider services, CancellationToken cancellationToken)
    {
        var query = (TQuery)request;
        IQueryHandler<TQuery, TResponse> handler =
            services.GetService<IQueryHandler<TQuery, TResponse>>()
            ?? throw new InvalidOperationException(
                $"No handler registered for query '{typeof(TQuery).Name}'. " +
                $"Expected an IQueryHandler<{typeof(TQuery).Name}, {typeof(TResponse).Name}> " +
                "registered via AddHandlersFromAssembly.");

        return BehaviorPipeline.ExecuteAsync(query, handler.HandleAsync, services, cancellationToken);
    }
}

/// <summary>
/// Composes registered <see cref="IPipelineBehavior{TRequest, TResponse}"/> instances
/// around the handler. First registered runs outermost.
/// </summary>
internal static class BehaviorPipeline
{
    public static Task<TResponse> ExecuteAsync<TRequest, TResponse>(
        TRequest request,
        Func<TRequest, CancellationToken, Task<TResponse>> handler,
        IServiceProvider services,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        PipelineNext<TResponse> next = () => handler(request, cancellationToken);

        foreach (IPipelineBehavior<TRequest, TResponse> behavior in
                 services.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse())
        {
            PipelineNext<TResponse> current = next;
            IPipelineBehavior<TRequest, TResponse> wrapped = behavior;
            next = () => wrapped.HandleAsync(request, current, cancellationToken);
        }

        return next();
    }
}
