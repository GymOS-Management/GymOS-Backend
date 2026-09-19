using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.WebApi.Middleware;

/// <summary>
/// R14/D2: resolves which tenant the request acts for, from the route, checked against the caller's
/// grants — <see cref="UserRole.TenantId"/>, where a null value is platform-wide and a value is a
/// grant for that one gym.
/// </summary>
/// <remarks>
/// <para>H1 / grants spec D1: runs after authentication and before authorization, because
/// <c>IPermissionEvaluator</c> now depends on the tenant this sets, and both the maintenance middleware
/// and authorization ask it. It runs before anything opens a connection to a tenant-scoped table, too,
/// because <c>TenantSessionInterceptor</c> writes whatever this leaves on <see cref="ITenantContext"/>
/// on every connection open.</para>
/// <para><b>Anonymous passes through, unresolved.</b> Running ahead of authorization means this
/// middleware, not authorization, is the first thing an anonymous caller on a gym route reaches. It sets
/// no tenant and performs no grant check — there is no caller to check grants for — and lets the request
/// continue so authorization still challenges it with 401. See grants spec D2 and
/// <c>TenantResolutionTests.AnAnonymousGymRouteIsChallenged_NotHidden</c>. This early return assumes
/// every <c>{gymId}</c> route requires authorization — a <c>{gymId}</c> route marked
/// <c>AllowAnonymous</c> would run with no tenant and no grant check at all, and
/// <c>ApiEndpointTests</c>'s secured-route count (65) is the tripwire that guards that
/// assumption.</para>
/// <para><b>404, never 403, for an authenticated caller with no grant for the gym.</b> "You may not see
/// this gym" and "this gym does not exist" must be indistinguishable, or the status code becomes an
/// oracle for enumerating tenants. The response carries no body written here — the framework's
/// status-code-pages handler (already wired in <c>Program.cs</c> via <c>AddProblemDetails</c> +
/// <c>UseStatusCodePages()</c>) fills in the same RFC 7807 shape a "not found" from a handler would, so
/// a caller cannot tell the two apart from the response either.</para>
/// <para><b>The grant check is a raw <see cref="UserRole"/> query, and must stay one.</b> It cannot go
/// through <c>IPermissionEvaluator</c>: that evaluator's answer now depends on the tenant this method is
/// the one setting, so asking it here would be circular.</para>
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    private const string GymIdRouteKey = "gymId";

    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser currentUser,
        ITenantContextSetter tenantContextSetter,
        AppDbContext dbContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(currentUser);
        ArgumentNullException.ThrowIfNull(tenantContextSetter);
        ArgumentNullException.ThrowIfNull(dbContext);

        object? routeValue = context.Request.RouteValues[GymIdRouteKey];

        if (routeValue is null)
        {
            // No `{gymId}` in this route: platform scope. Every endpoint built so far is exactly this.
            await _next(context);
            return;
        }

        // Every gym route declares `{gymId:guid}`, so routing itself already refused a malformed id
        // with 404 before this middleware runs — this branch is currently unreachable through HTTP.
        // Kept anyway: a route value present but not a Guid must not fall through to platform scope,
        // which would be a silent widening of what a malformed id reaches, for the day a route omits
        // the constraint.
        if (routeValue is not string rawGymId || !Guid.TryParse(rawGymId, out Guid gymId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!currentUser.IsAuthenticated)
        {
            // Grants spec D2: an anonymous caller on a gym route is challenged by authorization (401),
            // not told the gym does not exist. Nothing is resolved here — no grant check, no tenant —
            // because there is no caller to resolve it for.
            await _next(context);
            return;
        }

        if (!await CallerHoldsAGrantForAsync(currentUser, dbContext, gymId, context.RequestAborted))
        {
            // 404, not 403 — see the remarks above.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        tenantContextSetter.SetTenant(gymId);

        await _next(context);
    }

    /// <summary>
    /// Whether the caller holds a platform-wide grant (<see cref="UserRole.TenantId"/> is null) or a
    /// grant naming this gym specifically.
    /// </summary>
    /// <remarks>
    /// Reached only for an authenticated caller — <see cref="InvokeAsync"/> returns before this call for
    /// anyone else. The <c>UserId is not {}</c> guard stays anyway: it is the difference between "no
    /// grant" and "no caller", and costs nothing to keep true if that invariant ever changes.
    /// </remarks>
    private static async Task<bool> CallerHoldsAGrantForAsync(
        ICurrentUser currentUser, AppDbContext dbContext, Guid gymId, CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return false;
        }

        return await dbContext.Set<UserRole>()
            .AsNoTracking()
            .AnyAsync(
                assignment => assignment.UserId == userId
                    && (assignment.TenantId == null || assignment.TenantId == gymId),
                cancellationToken);
    }
}
