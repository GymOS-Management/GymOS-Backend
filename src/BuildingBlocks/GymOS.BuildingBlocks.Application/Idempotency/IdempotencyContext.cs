namespace GymOS.BuildingBlocks.Application.Idempotency;

/// <summary>
/// Scoped carrier between the host edge and the pipeline: the edge sets the caller's
/// Idempotency-Key on the way in; the behavior sets the response flags on the way out
/// (same write-once pattern as CorrelationContext).
/// </summary>
public sealed class IdempotencyContext
{
    public string? Key { get; private set; }

    public bool WasReplayed { get; private set; }

    public TimeSpan? RetryAfter { get; private set; }

    /// <summary>Called once per scope by the owning edge.</summary>
    public void SetKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (Key is not null)
        {
            throw new InvalidOperationException(
                "The idempotency key for this scope is already set — exactly one edge owns it.");
        }

        Key = key;
    }

    public void MarkReplayed() => WasReplayed = true;

    public void SetRetryAfter(TimeSpan retryAfter) => RetryAfter = retryAfter;
}
