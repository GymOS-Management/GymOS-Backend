using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;

namespace GymOS.WebApi.Middleware;

/// <summary>
/// Maps the exceptions that carry a meaningful HTTP answer onto RFC 7807 responses, and
/// lets everything else fall through to the framework's 500.
///
/// The list is short on purpose. Business failures are <c>Result</c> values, not exceptions
/// (house pattern), so only two kinds reach here: a permission check the endpoint policy
/// should already have made, and a domain invariant breach — which is a defect, reported as
/// one.
/// </summary>
public static class ExceptionHandlingExtensions
{
    public static IServiceCollection AddGymOSExceptionHandling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddExceptionHandler<PermissionDeniedExceptionHandler>();
        services.AddExceptionHandler<DomainExceptionHandler>();

        return services;
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
            if (exception is not DomainException)
            {
                return false;
            }

            await Results.Problem(
                title: "Unexpected error",
                detail: "The request could not be completed.",
                statusCode: StatusCodes.Status500InternalServerError,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "server.invariant_violation",
                })
                .ExecuteAsync(httpContext).ConfigureAwait(false);

            return true;
        }
    }
}
