using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Identity;
using Microsoft.AspNetCore.RateLimiting;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// Rate limiting for the whole API (ARCHITECTURE.md §3.1, M7 spec §7): a global limiter that
/// partitions every request, plus the sign-in surface's own tighter policy.
///
/// <list type="bullet">
/// <item>the <b>global limiter</b> (<see cref="AddGymOSRateLimiting"/>'s <c>GlobalLimiter</c>) caps an
/// authenticated caller <b>per user id</b> and an anonymous one <b>per client IP</b> — a signed-in caller
/// keeps their own budget no matter which address they call from, and an anonymous one is throttled the
/// only way it can be identified. <c>/health*</c> and <c>/version</c> carry no limiter at all, because a
/// load balancer or another replica must always be able to ask;</item>
/// <item>the <see cref="SignInPolicy"/> caps sign-in attempts <b>per client IP</b> — it blunts credential
/// stuffing, where one source tries many accounts, and it costs nothing to evaluate because it never
/// touches the database. The domain's lockout (<c>IUserRepository.RecordFailedLoginAsync</c>, thresholds
/// in <c>Security:LoginPolicy</c>) caps attempts <b>per account</b> — it blunts brute forcing of one
/// account, and it works even when the attacker rotates IPs, which is precisely what an IP limiter cannot
/// see. A single composite IP+email key would need the request body read inside the partition selector,
/// before model binding — buffering every sign-in request to duplicate protection the account lockout
/// already provides.</item>
/// </list>
///
/// Every number — <see cref="RateLimitOptions.ApiRequestsPerMinute"/>,
/// <see cref="RateLimitOptions.AnonymousRequestsPerMinute"/>,
/// <see cref="RateLimitOptions.SignInAttemptsPerWindow"/>, <see cref="RateLimitOptions.SignInWindowSeconds"/>
/// — is read from <see cref="ISettings{T}"/> inside the partition factory, once per request, rather than
/// captured once at startup: a saved setting is live. The framework still caches one limiter instance per
/// partition key, though, so a changed number applies to a <b>new</b> partition (a user or address not
/// currently tracked) immediately, and to an <b>existing</b> partition only once its current window rolls
/// over — an in-flight window finishes out under the limit that was in effect when it opened.
/// </summary>
public static class RateLimitingExtensions
{
    public const string SignInPolicy = "sign-in";

    /// <summary>
    /// <see cref="SlidingWindowRateLimiter"/> — the global limiter's per-user and per-IP
    /// partitions, below — never reports <see cref="MetadataName.RetryAfter"/> on a rejected lease
    /// (verified against this SDK's <c>System.Threading.RateLimiting</c>; <see cref="FixedWindowRateLimiter"/>,
    /// which the sign-in policy uses, always does). This is the fallback for that gap: one segment
    /// of the global limiter's fixed one-minute, six-segment window below — the shortest interval
    /// guaranteed to have freed some capacity.
    /// </summary>
    private static readonly TimeSpan GlobalLimiterFallbackRetryAfter = TimeSpan.FromSeconds(10);

    public static IServiceCollection AddGymOSRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Tell the caller when to come back: a 429 without Retry-After leaves a
            // well-behaved client guessing and a badly-behaved one hammering.
            options.OnRejected = async (context, cancellationToken) =>
            {
                TimeSpan retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan metadataRetryAfter)
                    ? metadataRetryAfter
                    : GlobalLimiterFallbackRetryAfter;

                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                await Results.Problem(
                    title: "Too many requests",
                    detail: "The rate limit for this caller has been reached. Retry after the interval "
                        + "in the Retry-After header.",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    type: "security.rate_limited",
                    extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["code"] = "security.rate_limited",
                    })
                    .ExecuteAsync(context.HttpContext).ConfigureAwait(false);
            };

            options.AddPolicy(SignInPolicy, httpContext =>
            {
                RateLimitOptions limits = httpContext.RequestServices
                    .GetRequiredService<ISettings<RateLimitOptions>>().Current;

                return RateLimitPartition.GetFixedWindowLimiter(
                    // Unknown remote address (a misconfigured proxy) shares one bucket
                    // rather than bypassing the limit — fail closed, not open.
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limits.SignInAttemptsPerWindow,
                        Window = TimeSpan.FromSeconds(limits.SignInWindowSeconds),
                        QueueLimit = 0, // reject immediately; a queued sign-in is a slow failure
                    });
            });

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                PathString path = httpContext.Request.Path;
                if (path.StartsWithSegments("/health") || path.Equals("/version"))
                {
                    return RateLimitPartition.GetNoLimiter("open");
                }

                RateLimitOptions limits = httpContext.RequestServices
                    .GetRequiredService<ISettings<RateLimitOptions>>().Current;

                // Authenticated: per user id, so a caller keeps one budget no matter which address they
                // call from. Anonymous: per client IP, the only identity available.
                string? userId = httpContext.User.FindFirstValue(GymOSClaims.Subject);
                return userId is not null
                    ? RateLimitPartition.GetSlidingWindowLimiter(
                        $"user:{userId}",
                        _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = limits.ApiRequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 6,
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetSlidingWindowLimiter(
                        $"ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
                        _ => new SlidingWindowRateLimiterOptions
                        {
                            PermitLimit = limits.AnonymousRequestsPerMinute,
                            Window = TimeSpan.FromMinutes(1),
                            SegmentsPerWindow = 6,
                            QueueLimit = 0,
                        });
            });
        });

        return services;
    }
}
