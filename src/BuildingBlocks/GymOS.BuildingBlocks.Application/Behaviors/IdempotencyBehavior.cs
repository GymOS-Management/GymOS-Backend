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

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Lease for '{OperationKey}' (key '{IdempotencyKey}') was lost to a takeover before the outcome " +
            "could be recorded — this attempt's effects were rolled back; the new holder owns the outcome.")]
    private static partial void LogLeaseLostAtFinish(ILogger logger, string operationKey, string idempotencyKey);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Releasing the claim for '{OperationKey}' (key '{IdempotencyKey}') failed; the claim stays until " +
            "its lease expires. The handler's original exception is rethrown, not this one.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string operationKey, string idempotencyKey);

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
        Guid claimToken = result.ClaimToken ?? Guid.Empty;

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

            Guid? takeoverToken = await _store
                .TryTakeOverAsync(command.OperationKey, key, hash, now + _options.Value.LeaseDuration, cancellationToken)
                .ConfigureAwait(false);

            if (takeoverToken is null)
            {
                // Lost the takeover race: someone else re-leased it a moment ago.
                _context.SetRetryAfter(_options.Value.LeaseDuration);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            claimToken = takeoverToken.Value;
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

            bool marked = await _store
                .MarkFinishedAsync(command.OperationKey, key, claimToken, finalStatus, responseStatus, body, cancellationToken)
                .ConfigureAwait(false);

            if (!marked)
            {
                // The fence tripped: this attempt outlived its lease and someone else took the
                // claim over (new token). The other holder owns the outcome, so this attempt's
                // business effect must not commit — roll it back and report the conflict.
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                LogLeaseLostAtFinish(_logger, command.OperationKey, key);
                _context.SetRetryAfter(_options.Value.LeaseDuration);
                return Fail(IdempotencyErrors.OperationInProgress);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return response;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                // False (claim taken over, not ours to delete anymore) is fine — the point is
                // only that a still-owned claim does not outlive a transient failure.
                await _store.ReleaseClaimAsync(command.OperationKey, key, claimToken, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception releaseException)
            {
                // Never mask the handler's original exception with a release failure; the claim
                // simply stays until its lease expires and a retry takes it over.
                LogReleaseFailed(_logger, releaseException, command.OperationKey, key);
            }

            throw;
        }
    }

    private static TResponse Fail(Error error) => StoredOutcomeCodec.Failure<TResponse>(error);
}
