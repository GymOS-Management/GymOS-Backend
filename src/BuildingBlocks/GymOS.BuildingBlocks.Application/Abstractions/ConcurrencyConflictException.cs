namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The row a caller intended to update changed after they read it (ARCHITECTURE.md §3:
/// optimistic concurrency, 409 on conflict).
///
/// <para><b>Why an exception rather than a failed <c>Result</c>.</b> Business failures are values
/// in this codebase, but a concurrency conflict is not a decision any handler made — the handler
/// ran to completion and the store refused the write. It is detected by the persistence layer,
/// several frames below whoever could return a <c>Result</c>, and every handler would otherwise
/// need identical plumbing to translate it. One exception, mapped once at the HTTP edge, keeps
/// that out of every use case.</para>
///
/// <para>It is not a defect, so unlike <c>DomainException</c> it maps to 409 and not 500: the
/// caller did nothing wrong and the remedy is theirs — re-read, re-apply, retry.</para>
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    private const string DefaultMessage =
        "The resource was modified by someone else after it was read. " +
        "Re-read it, re-apply the change, and retry.";

    public ConcurrencyConflictException()
        : base(DefaultMessage)
    {
    }

    public ConcurrencyConflictException(string message)
        : base(message)
    {
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ConcurrencyConflictException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
