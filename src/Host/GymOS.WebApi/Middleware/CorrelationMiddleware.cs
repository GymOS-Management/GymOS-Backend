using GymOS.BuildingBlocks.Infrastructure.Context;
using Serilog.Context;

namespace GymOS.WebApi.Middleware;

/// <summary>
/// The HTTP edge of the correlation spine (AD-38): accepts a caller-supplied
/// <c>X-Correlation-Id</c> (valid GUID) or mints one, echoes it on the response,
/// stores it in the request scope for the outbox and handlers, and pushes it into
/// every log line of the request.
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, CorrelationContext correlationContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(correlationContext);

        Guid correlationId = ResolveCorrelationId(context.Request.Headers[HeaderName]);

        correlationContext.Set(correlationId);
        context.Response.Headers[HeaderName] = correlationId.ToString();

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    /// <summary>A caller-supplied valid GUID is honored; anything else gets a fresh id.</summary>
    internal static Guid ResolveCorrelationId(string? headerValue) =>
        Guid.TryParse(headerValue, out Guid supplied) && supplied != Guid.Empty
            ? supplied
            : Guid.NewGuid();
}
