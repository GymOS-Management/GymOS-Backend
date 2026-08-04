namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// The acting caller — who they are and how they reached us — resolved at the edge (HTTP
/// request now; job runner and outbox processor when they land).
///
/// <see cref="UserId"/> is <see langword="null"/> for work no human initiated: startup
/// seeding, background jobs, event handlers. That is why the audit and outbox
/// <c>user_id</c> columns are nullable by design (AD-14), and why the audit row also keeps
/// a denormalized email — a log entry has to stay readable after the account is gone.
///
/// This reports identity, not entitlement. It deliberately exposes no permission set:
/// permissions are not in the access token (AD-4 carries only their hash), so answering
/// "may they?" is a query, and that lives behind <see cref="IPermissionEvaluator"/>.
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }

    string? Email { get; }

    bool IsAuthenticated { get; }

    /// <summary>
    /// Caller IP for the audit trail. Recorded as reported, never trusted for
    /// authorization — proxy header handling is deployment configuration, not evidence.
    /// </summary>
    string? IpAddress { get; }

    string? UserAgent { get; }
}
