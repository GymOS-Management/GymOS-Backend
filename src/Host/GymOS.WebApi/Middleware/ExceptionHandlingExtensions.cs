using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Rules;
using Microsoft.AspNetCore.Diagnostics;

namespace GymOS.WebApi.Middleware;

/// <summary>
/// Maps the exceptions that carry a meaningful HTTP answer onto RFC 7807 responses, and
/// lets everything else fall through to the framework's 500.
///
/// The list is short on purpose. Business failures are <c>Result</c> values, not exceptions
/// (house pattern), so only three kinds reach here: a permission check the endpoint policy should
/// already have made, a concurrency conflict the store detected below any handler, and a domain
/// invariant breach — which is a defect, reported as one.
/// </summary>
public static class ExceptionHandlingExtensions
{
    public static IServiceCollection AddGymOSExceptionHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // First on purpose: a cancellation can surface from any depth of the pipeline, and the
        // answer never depends on what the request was doing when the caller left.
        services.AddExceptionHandler<CanceledRequestExceptionHandler>();

        services.AddExceptionHandler<PermissionDeniedExceptionHandler>();
        services.AddExceptionHandler<ConcurrencyConflictExceptionHandler>();
        services.AddExceptionHandler<DomainExceptionHandler>();

        return services;
    }

    /// <summary>
    /// The caller hung up — a refresh, a navigation, an aborted fetch — while the request was in
    /// flight, and the cancellation surfaced as an exception. That is the framework correctly
    /// abandoning work nobody is waiting on, not a failure of ours, so it must not be recorded
    /// as a 500. With the database across a network, every request holds queries long enough for
    /// aborts to land mid-flight; before this handler, each one polluted the error log.
    ///
    /// <para>Internal (not private) so the contract has a direct test.</para>
    /// </summary>
    internal sealed class CanceledRequestExceptionHandler : IExceptionHandler
    {
        /// <summary>
        /// nginx's "client closed request". No standard code exists for this; 499 is the
        /// convention log pipelines already understand, and no body travels — there is nobody
        /// left to read one.
        /// </summary>
        internal const int ClientClosedRequest = 499;

        public ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            // Only when the CALLER aborted. An OperationCanceledException on a request whose
            // caller is still waiting is a genuine defect and falls through to the 500 it
            // deserves.
            if (exception is not OperationCanceledException
                || !httpContext.RequestAborted.IsCancellationRequested)
            {
                return ValueTask.FromResult(false);
            }

            httpContext.Response.StatusCode = ClientClosedRequest;
            return ValueTask.FromResult(true);
        }
    }

    /// <summary>
    /// Someone else changed the resource between the caller's read and their write
    /// (ARCHITECTURE.md §3).
    ///
    /// The response says what to do about it, because "409 Conflict" alone leaves a client
    /// guessing whether to retry, and a blind retry loop against a stale version never succeeds.
    /// </summary>
    private sealed class ConcurrencyConflictExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is not ConcurrencyConflictException)
            {
                return false;
            }

            await Results.Problem(
                title: "Conflict",
                detail:
                    "The resource changed after you read it, so the update was not applied. " +
                    "Re-read the resource, re-apply your change to the current state, and retry " +
                    "with the new rowVersion. Retrying with the same version will always fail.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "concurrency.conflict",

                    // Explicitly false: a conflict is not transient, and a client that retries
                    // unchanged will loop forever.
                    ["retryable"] = false,
                })
                .ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }
    }

    /// <summary>
    /// A request that reached its handler without the declared permission. The caller gets
    /// the same 403 the endpoint policy would have produced — the misconfiguration is our
    /// problem to fix, not theirs to decipher — while the behavior logs it as an error.
    /// </summary>
    private sealed class PermissionDeniedExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is not PermissionDeniedException denied)
            {
                return false;
            }

            await Results.Problem(
                title: "Forbidden",
                detail: "You do not have permission to perform this action.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "auth.permission_denied",

                    // The permission name is safe to return and saves a support round-trip;
                    // it is a declared, documented capability, not a secret.
                    ["requiredPermission"] = denied.Permission,
                })
                .ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }
    }

    /// <summary>
    /// A broken domain invariant means the application let through a state the domain
    /// forbids — a bug on our side. Reported as 500 with the stable code, never as a 400:
    /// blaming the caller for our defect sends them off debugging a correct request.
    /// </summary>
    private sealed class DomainExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(
            HttpContext httpContext,
            Exception exception,
            CancellationToken cancellationToken)
        {
            if (exception is not DomainException domainException)
            {
                return false;
            }

            // A broken business rule that reached here was not validated by the handler that should
            // have — still our defect, still a 500, but naming the rule turns an opaque incident
            // into a one-line diagnosis.
            string? brokenRule = (domainException as BusinessRuleViolationException)?.Code;

            await Results.Problem(
                title: "Unexpected error",
                detail: "The request could not be completed.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "server.invariant_violation",
                    ["brokenRule"] = brokenRule,
                })
                .ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }
    }
}
