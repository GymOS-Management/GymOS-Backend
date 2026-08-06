using GymOS.BuildingBlocks.Application.Idempotency;

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
/// The owning edge for IdempotencyContext: copies the caller's Idempotency-Key inward and, on
/// the way out, stamps Idempotency-Replayed / Retry-After from what the pipeline recorded.
/// </summary>
public sealed class IdempotencyHeaderMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware with the next delegate in the pipeline.</summary>
    public IdempotencyHeaderMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Copies the request's idempotency key inward and stamps replay/backoff headers outward.</summary>
    public async Task InvokeAsync(HttpContext httpContext, IdempotencyContext idempotencyContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(idempotencyContext);

        string? key = httpContext.Request.Headers[IdempotencyHeaders.HeaderName].FirstOrDefault();
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
}
