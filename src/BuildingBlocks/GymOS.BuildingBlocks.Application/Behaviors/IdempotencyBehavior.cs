using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Idempotency;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.SharedKernel.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.BuildingBlocks.Application.Behaviors;

/// <summary>
/// AD-39's decision table. Pipeline position: inside Authorization (a stored response is never
/// returned to a caller who could not execute the operation now), outside the handler.
/// The claim autocommits; the handler runs inside an explicit transaction this behavior opens,
/// and the record flips to Completed in that same transaction — effect and record are atomic.
/// </summary>
public sealed partial class IdempotencyBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
    where TResponse : Result
{
    private readonly IIdempotencyStore _store;
    private readonly IdempotencyContext _context;
    private readonly IClock _clock;
    private readonly IOptions<IdempotencyOptions> _options;
    private readonly ILogger<IdempotencyBehavior<TRequest, TResponse>> _logger;

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Replayed stored outcome for '{OperationKey}' (key '{IdempotencyKey}').")]
    private static partial void LogReplayed(ILogger logger, string operationKey, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Idempotency key reused with a different request for '{OperationKey}' — client bug.")]
    private static partial void LogKeyReuseMismatch(ILogger logger, string operationKey);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Took over an expired claim for '{OperationKey}' (key '{IdempotencyKey}') — prior attempt never committed.")]
    private static partial void LogTakeover(ILogger logger, string operationKey, string idempotencyKey);

    /// <summary>Creates the behavior with its store, scoped context, clock, options, and logger dependencies.</summary>
    public IdempotencyBehavior(
        IIdempotencyStore store,
        IdempotencyContext context,
        IClock clock,
        IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    {
        _store = store;
        _context = context;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Runs AD-39's decision table for <paramref name="request"/>: non-idempotent commands and
    /// idempotent commands without a supplied key (when not required) pass straight through;
    /// otherwise the behavior claims the key, replays a stored outcome, rejects a mismatched or
    /// in-flight duplicate, or executes <paramref name="next"/> and records the outcome atomically
    /// with the handler's own transaction.
    /// </summary>
    public async Task<TResponse> HandleAsync(
        TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        if (request is not IIdempotentCommand command)
        {
            return await next().ConfigureAwait(false);
        }

        string? key = _context.Key;
        if (key is null)
        {
            return command.KeyRequired
                ? Fail(IdempotencyErrors.KeyRequired)
                : await next().ConfigureAwait(false);
        }

        string hash = CanonicalRequestHasher.Hash(request);
        DateTimeOffset now = _clock.UtcNow;
        var claim = new IdempotencyClaimRequest(
            command.OperationKey, key, hash,
            LeaseExpiresAt: now + _options.Value.LeaseDuration,
            CreatedAt: now,
            ExpiresAt: now + command.RetentionPeriod);

        IdempotencyClaimResult result = await _store.TryClaimAsync(claim, cancellationToken).ConfigureAwait(false);

        if (!result.Claimed)
        {
            IdempotencyRecordSnapshot existing = result.Existing!;

            if (existing.Status is IdempotencyRecordStatus.Completed or IdempotencyRecordStatus.FailedDeterministic)
            {
                if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
                {
                    LogKeyReuseMismatch(_logger, command.OperationKey);
                    return Fail(IdempotencyErrors.KeyReuseMismatch);
                }

                _context.MarkReplayed();
                LogReplayed(_logger, command.OperationKey, key);
                return StoredOutcomeCodec.Decode<TResponse>(existing.ResponseStatus, existing.ResponseBody);
            }

            // InProgress.
            if (existing.LeaseExpiresAt > now)
            {
                TimeSpan retryAfter = existing.LeaseExpiresAt - now;
                _context.SetRetryAfter(retryAfter < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : retryAfter);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                LogKeyReuseMismatch(_logger, command.OperationKey);
                return Fail(IdempotencyErrors.KeyReuseMismatch);
            }

            bool takenOver = await _store
                .TryTakeOverAsync(command.OperationKey, key, hash, now + _options.Value.LeaseDuration, cancellationToken)
                .ConfigureAwait(false);

            if (!takenOver)
            {
                // Lost the takeover race: someone else re-leased it a moment ago.
                _context.SetRetryAfter(_options.Value.LeaseDuration);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            LogTakeover(_logger, command.OperationKey, key);
        }

        await using IIdempotencyTransaction transaction =
            await _store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            TResponse response = await next().ConfigureAwait(false);

            (int responseStatus, string? body) = StoredOutcomeCodec.Encode(response);
            IdempotencyRecordStatus finalStatus = response.IsSuccess
                ? IdempotencyRecordStatus.Completed
                : IdempotencyRecordStatus.FailedDeterministic;

            await _store.MarkFinishedAsync(command.OperationKey, key, finalStatus, responseStatus, body, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            await _store.ReleaseClaimAsync(command.OperationKey, key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static TResponse Fail(Error error) => StoredOutcomeCodec.Failure<TResponse>(error);
}
