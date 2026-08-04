namespace GymOS.SharedKernel.Results;

/// <summary>
/// A typed business error: a stable machine-readable code, a human-readable message,
/// and a <see cref="ErrorKind"/> for mechanical mapping at boundaries.
/// </summary>
public sealed record Error
{
    private Error(string code, string message, ErrorKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        Kind = kind;
    }

    /// <summary>Stable machine-readable code, e.g. <c>gyms.slug_taken</c>.</summary>
    public string Code { get; }

    public string Message { get; }

    public ErrorKind Kind { get; }

    public static Error Failure(string code, string message) => new(code, message, ErrorKind.Failure);

    public static Error Validation(string code, string message) => new(code, message, ErrorKind.Validation);

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);
}
