using GymOS.BuildingBlocks.Application.Idempotency;
using Microsoft.Extensions.Primitives;

namespace GymOS.WebApi.Middleware;

/// <summary>Names the wire artifacts of AD-39 (header in, replay/backoff headers out).</summary>
public static class IdempotencyHeaders
{
    /// <summary>The caller-supplied header carrying the idempotency key.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Response header stamped when the request returned a stored response instead of executing.</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";
}

/// <summary>
/// The owning edge for IdempotencyContext: validates and copies the caller's Idempotency-Key
/// inward and, on the way out, stamps Idempotency-Replayed / Retry-After from what the pipeline
/// recorded. A malformed key (repeated header, or longer than the record column) is rejected
/// here with 400 <c>idempotency.key_invalid</c> — before it can 500 against the varchar(200)
/// column or silently pick one of two conflicting values.
/// </summary>
public sealed class IdempotencyHeaderMiddleware
{
    /// <summary>Maximum accepted key length — the record table's idempotency_key is varchar(200).</summary>
    public const int MaxKeyLength = 200;

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware with the next delegate in the pipeline.</summary>
    public IdempotencyHeaderMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Copies the request's idempotency key inward and stamps replay/backoff headers outward.</summary>
    public async Task InvokeAsync(HttpContext httpContext, IdempotencyContext idempotencyContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(idempotencyContext);

        StringValues values = httpContext.Request.Headers[IdempotencyHeaders.HeaderName];

        if (values.Count > 1)
        {
            await RejectInvalidKeyAsync(httpContext,
                "The Idempotency-Key header appeared more than once. Send exactly one value.")
                .ConfigureAwait(false);
            return;
        }

        string? key = values.FirstOrDefault();

        if (key is not null && key.Length > MaxKeyLength)
        {
            await RejectInvalidKeyAsync(httpContext,
                $"The Idempotency-Key header exceeds {MaxKeyLength} characters. Use a shorter key — a UUID is recommended.")
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(key))
        {
            idempotencyContext.SetKey(key);
        }

        httpContext.Response.OnStarting(() =>
        {
            if (idempotencyContext.WasReplayed)
            {
                httpContext.Response.Headers[IdempotencyHeaders.ReplayedHeaderName] = "true";
            }

            if (idempotencyContext.RetryAfter is { } retryAfter)
            {
                httpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.CompletedTask;
        });

        await _next(httpContext);
    }

    /// <summary>
    /// Short-circuits with the RFC 7807 shape the rest of the API uses (stable machine code in
    /// the <c>code</c> extension, per API_STANDARDS §10) — a middleware-level rejection must be
    /// indistinguishable in shape from a pipeline one.
    /// </summary>
    private static Task RejectInvalidKeyAsync(HttpContext httpContext, string detail) =>
        Results.Problem(
            title: "Validation failed",
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "idempotency.key_invalid",
            })
            .ExecuteAsync(httpContext);
}
