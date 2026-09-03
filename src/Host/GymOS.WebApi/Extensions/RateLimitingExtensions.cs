using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// Rate limiting for the sign-in surface (ARCHITECTURE.md §3.1).
///
/// Throttling is deliberately split in two, because the two halves defend against
/// different attacks and one mechanism cannot do both:
/// <list type="bullet">
/// <item>this limiter caps attempts <b>per client IP</b> — it blunts credential
/// stuffing, where one source tries many accounts, and it costs nothing to evaluate
/// because it never touches the database;</item>
/// <item>the domain's lockout (<c>IUserRepository.RecordFailedLoginAsync</c>, thresholds in
/// <c>Security:LoginPolicy</c>) caps attempts <b>per account</b> — it blunts brute
/// forcing of one account, and it works even when the attacker rotates IPs, which is
/// precisely what an IP limiter cannot see.</item>
/// </list>
/// A single composite IP+email key would need the request body read inside the
/// partition selector, before model binding — buffering every sign-in request to
/// duplicate protection the account lockout already provides.
/// </summary>
public static class RateLimitingExtensions
{
    public const string SignInPolicy = "sign-in";

    public static IServiceCollection AddGymOSRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        int permitLimit = configuration.GetValue("Security:RateLimiting:SignInAttemptsPerWindow", 10);
        TimeSpan window = configuration.GetValue("Security:RateLimiting:SignInWindow", TimeSpan.FromMinutes(1));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Tell the caller when to come back: a 429 without Retry-After leaves a
            // well-behaved client guessing and a badly-behaved one hammering.
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(SignInPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
                // Unknown remote address (a misconfigured proxy) shares one bucket
                // rather than bypassing the limit — fail closed, not open.
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    QueueLimit = 0, // reject immediately; a queued sign-in is a slow failure
                }));
        });

        return services;
    }
}
