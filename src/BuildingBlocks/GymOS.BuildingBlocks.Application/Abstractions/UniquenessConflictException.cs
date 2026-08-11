namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// A unique index refused the write: another row already holds the value this one claimed
/// (a gym slug, a normalized email, a custom domain, the one-live-subscription-per-gym index).
///
/// <para><b>Why this exists as its own exception.</b> Handlers check uniqueness before writing —
/// and that check is a courtesy, not the guarantee: between the check and the insert another
/// caller can take the value, which is exactly why the index is there. When the index wins, the
/// provider's error is a defect-shaped <c>DbUpdateException</c>; untranslated, it reached the
/// caller as an opaque 500 for something that is not a server fault at all. Translated here, it
/// maps to the same 409 the pre-check would have produced.</para>
///
/// <para>Same reasoning as <see cref="ConcurrencyConflictException"/>: detected far below any
/// handler that could return a <c>Result</c>, so it travels as an exception and is mapped once at
/// the HTTP edge rather than plumbed through every use case.</para>
/// </summary>
public sealed class UniquenessConflictException : Exception
{
    private const string DefaultMessage =
        "Another record already uses one of the values in this request. " +
        "Re-read the resource and choose a value that is still free.";

    public UniquenessConflictException()
        : base(DefaultMessage)
    {
    }

    public UniquenessConflictException(string message)
        : base(message)
    {
    }

    public UniquenessConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="constraintName">
    /// The database constraint that refused the write, when the provider reports one. Returned to
    /// the caller: these names are declared in migrations and reviewed, and knowing *which*
    /// uniqueness failed is the difference between "try another slug" and a support ticket.
    /// </param>
    public UniquenessConflictException(Exception innerException, string? constraintName)
        : base(DefaultMessage, innerException) => ConstraintName = constraintName;

    public string? ConstraintName { get; }
}
