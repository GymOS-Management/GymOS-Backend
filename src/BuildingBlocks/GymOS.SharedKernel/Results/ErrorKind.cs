namespace GymOS.SharedKernel.Results;

/// <summary>
/// Classifies an <see cref="Error"/> so boundaries can map it mechanically —
/// the HTTP layer maps kinds to status codes (RFC 7807), handlers never throw
/// for business outcomes.
/// </summary>
public enum ErrorKind
{
    /// <summary>Unspecified business failure (maps to 400/500 per context).</summary>
    Failure = 0,

    /// <summary>Input failed validation (422/400).</summary>
    Validation = 1,

    /// <summary>The referenced resource does not exist (404).</summary>
    NotFound = 2,

    /// <summary>The operation conflicts with current state (409).</summary>
    Conflict = 3,

    /// <summary>The caller is not authenticated (401).</summary>
    Unauthorized = 4,

    /// <summary>The caller lacks the required permission (403).</summary>
    Forbidden = 5,
}
