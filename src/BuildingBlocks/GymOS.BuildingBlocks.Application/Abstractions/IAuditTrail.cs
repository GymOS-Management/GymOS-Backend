namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Records the actions an entity diff cannot describe (AD-7). The interceptor captures
/// every row mutation automatically; this is for the facts that leave no row behind or
/// whose meaning is not visible in one — a sign-in, a failed sign-in, an export, a test
/// email.
///
/// Recording enlists in the current unit of work rather than writing immediately, so the
/// audit entry commits with whatever it describes: no audit of an action that rolled back,
/// and no action without its audit.
/// </summary>
public interface IAuditTrail
{
    /// <param name="action">Dotted, past tense: <c>auth.signed_in</c>, <c>auth.sign_in_failed</c>.</param>
    /// <param name="entityType">Aggregate the action concerns, when there is one.</param>
    /// <param name="entityId">Its id, when known — a failed sign-in on an unknown email has none.</param>
    /// <param name="details">
    /// Serialized into the audit row's new-values column. Must carry no secret: this lands
    /// in a table built to be read by humans and kept for years.
    /// </param>
    /// <param name="subjectEmail">
    /// Email to attribute the entry to when the actor is not (yet) authenticated — the
    /// address a failed sign-in was attempted against.
    /// </param>
    void Record(
        string action,
        string? entityType = null,
        Guid? entityId = null,
        IReadOnlyDictionary<string, string?>? details = null,
        string? subjectEmail = null);
}
