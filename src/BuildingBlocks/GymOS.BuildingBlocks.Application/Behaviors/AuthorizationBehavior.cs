using System.Collections.Concurrent;
using System.Reflection;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using Microsoft.Extensions.Logging;

namespace GymOS.BuildingBlocks.Application.Behaviors;

/// <summary>
/// Enforces <see cref="RequiresPermissionAttribute"/> on every dispatched request
/// (AD-5, second enforcement layer). Runs early in the pipeline — after validation, before
/// idempotency and the transaction — so an unauthorized call claims no idempotency key and
/// opens no transaction (AD-39 §1.4).
/// </summary>
public sealed partial class AuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ICurrentUser _currentUser;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly RequestPermissionCache _permissionCache;
    private readonly ILogger<AuthorizationBehavior<TRequest, TResponse>> _logger;

    [LoggerMessage(Level = LogLevel.Error, Message =
        "'{RequestName}' reached its handler without the endpoint policy for '{Permission}'. " +
        "The request was refused by the pipeline, but the endpoint mapping is missing its " +
        "RequireAuthorization and should be fixed.")]
    private static partial void LogMissingEndpointPolicy(ILogger logger, string requestName, string permission);

    public AuthorizationBehavior(
        ICurrentUser currentUser,
        IPermissionEvaluator permissionEvaluator,
        RequestPermissionCache permissionCache,
        ILogger<AuthorizationBehavior<TRequest, TResponse>> logger)
    {
        _currentUser = currentUser;
        _permissionEvaluator = permissionEvaluator;
        _permissionCache = permissionCache;
        _logger = logger;
    }

    public async Task<TResponse> HandleAsync(
        TRequest request,
        PipelineNext<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        string? permission = _permissionCache.PermissionFor(request.GetType());

        if (permission is null)
        {
            return await next().ConfigureAwait(false);
        }

        // An unauthenticated caller can hold nothing, so there is no point querying.
        bool allowed = _currentUser.IsAuthenticated &&
            await _permissionEvaluator.HasPermissionAsync(permission, cancellationToken).ConfigureAwait(false);

        if (!allowed)
        {
            LogMissingEndpointPolicy(_logger, request.GetType().Name, permission);
            throw new PermissionDeniedException(request.GetType().Name, permission);
        }

        return await next().ConfigureAwait(false);
    }
}

/// <summary>
/// Caches the declared permission per request type. Attribute lookup is reflection, and
/// the answer never changes for a given type — so it is resolved once and held on a DI
/// singleton, never a static field (rule R1: no static mutable state).
/// </summary>
public sealed class RequestPermissionCache
{
    private readonly ConcurrentDictionary<Type, string?> _permissions = new();

    /// <summary>The declared permission, or <see langword="null"/> when the request needs none.</summary>
    public string? PermissionFor(Type requestType)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        return _permissions.GetOrAdd(
            requestType,
            static type => type.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission);
    }
}
