using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// The one place a business <see cref="Result"/> becomes an HTTP response
/// (ARCHITECTURE.md §3: RFC 7807 <c>application/problem+json</c>).
///
/// Mapping lives here and only here so the status code for a given
/// <see cref="ErrorKind"/> cannot vary between endpoints — the kind of inconsistency
/// that forces clients to special-case individual routes.
/// </summary>
public static class ResultExtensions
{
    public static IResult ToProblem(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.IsSuccess)
        {
            throw new InvalidOperationException("A successful result cannot be converted to a problem response.");
        }

        Error error = result.Error!;

        int statusCode = StatusCodeByErrorCode.TryGetValue(error.Code, out int mapped)
            ? mapped
            : StatusCodeFor(error.Kind);

        return Results.Problem(
            title: TitleFor(statusCode, error.Kind),
            detail: error.Message,
            statusCode: statusCode,
            // The stable machine-readable code, in the RFC 7807 extension member
            // clients are meant to branch on. `detail` is for humans and may be reworded.
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = error.Code,
            });
    }

    /// <summary>
    /// Codes that map to a status their <see cref="ErrorKind"/> alone cannot express. Kept as an
    /// explicit, tiny table rather than widening ErrorKind: 428 is a fact about HTTP preconditions,
    /// not a new category of business failure the domain should learn about.
    /// </summary>
    private static readonly Dictionary<string, int> StatusCodeByErrorCode = new(StringComparer.Ordinal)
    {
        // "Send the precondition and try again" — distinct from 409, which means "you sent one and
        // it was stale". Telling those apart is the difference between a client bug and a race.
        ["concurrency.version_required"] = StatusCodes.Status428PreconditionRequired,
    };

    private static int StatusCodeFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TitleFor(int statusCode, ErrorKind kind) =>
        statusCode == StatusCodes.Status428PreconditionRequired
            ? "Precondition required"
            : TitleFor(kind);

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "Validation failed",
        ErrorKind.Unauthorized => "Authentication required",
        ErrorKind.Forbidden => "Forbidden",
        ErrorKind.NotFound => "Not found",
        ErrorKind.Conflict => "Conflict",
        _ => "Request failed",
    };
}
