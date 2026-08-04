using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// The platform's write-response convention, in one place (ARCHITECTURE.md §3: "Writes return the
/// updated resource; destructive ops return 204").
///
/// <list type="bullet">
/// <item><b>POST creating a resource</b> → <c>201 Created</c> + <c>Location</c> + the resource.</item>
/// <item><b>PUT / PATCH / POST acting on a resource</b> → <c>200 OK</c> + the resource.</item>
/// <item><b>DELETE, and actions with no resource to describe</b> → <c>204 No Content</c>.</item>
/// </list>
///
/// <para><b>Why return the resource rather than 204.</b> A 204 forces every client into a
/// read-after-write to learn what actually happened — and after an update that touched a computed
/// field, a server-side default, or the row version, the client's local copy is wrong until it
/// does. Worse for optimistic concurrency specifically: the caller needs the *new* version to make
/// a second edit, so a 204 guarantees a follow-up GET before every subsequent change.</para>
///
/// <para>Every response that carries a versioned resource also carries its <c>ETag</c>, so a client
/// can use the header or the body field and get the same answer.</para>
/// </summary>
public static class WriteResults
{
    /// <summary>201 for a created resource, with <c>Location</c> and the resource itself.</summary>
    public static IResult Created<TResource>(Result<TResource> result, string location, string? rowVersion)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsFailure
            ? result.ToProblem()
            : new ResourceResult<TResource>(TypedResults.Created(location, result.Value), rowVersion);
    }

    /// <summary>200 with the updated resource.</summary>
    public static IResult Updated<TResource>(Result<TResource> result, string? rowVersion)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsFailure
            ? result.ToProblem()
            : new ResourceResult<TResource>(TypedResults.Ok(result.Value), rowVersion);
    }

    /// <summary>200 with the current resource, plus its <c>ETag</c> for a later <c>If-Match</c>.</summary>
    public static IResult Read<TResource>(Result<TResource> result, string? rowVersion)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsFailure
            ? result.ToProblem()
            : new ResourceResult<TResource>(TypedResults.Ok(result.Value), rowVersion);
    }

    /// <summary>
    /// 204 for a write with no resource to return: a deletion, or an action whose whole effect is
    /// that something no longer applies.
    /// </summary>
    public static IResult NoContent(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    /// <summary>
    /// Attaches the <c>ETag</c> around an inner result. A wrapper rather than a mutation of
    /// <c>HttpContext.Response</c> at each call site: the header then cannot be forgotten on a new
    /// endpoint, and it is set before the body is written, which is the only point it still can be.
    /// </summary>
    private sealed class ResourceResult<TResource> : IResult
    {
        private readonly IResult _inner;
        private readonly string? _rowVersion;

        public ResourceResult(IResult inner, string? rowVersion)
        {
            _inner = inner;
            _rowVersion = rowVersion;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            if (!string.IsNullOrEmpty(_rowVersion))
            {
                httpContext.Response.Headers.ETag = RowVersion.ToETag(_rowVersion);
            }

            return _inner.ExecuteAsync(httpContext);
        }
    }
}

/// <summary>
/// Reads the <c>If-Match</c> precondition from a request.
/// </summary>
public static class PreconditionHeaders
{
    /// <summary>
    /// The raw <c>If-Match</c> value, or <see langword="null"/> when absent.
    ///
    /// Returned unvalidated on purpose: whether a missing precondition is acceptable depends on the
    /// operation, and whether a malformed one is a 400 is a decision for the Application layer that
    /// owns the error vocabulary — not for a header helper.
    /// </summary>
    public static string? IfMatch(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? value = request.Headers.IfMatch.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
