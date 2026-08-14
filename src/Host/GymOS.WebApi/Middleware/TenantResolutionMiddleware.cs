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
/// <para>Runs after <c>UseAuthorization()</c>, because it reads the authenticated caller's grants, and
/// before anything opens a connection to a tenant-scoped table, because <c>TenantSessionInterceptor</c>
/// writes whatever this leaves on <see cref="ITenantContext"/> on every connection open.</para>
/// <para><b>Inert today.</b> No route with a <c>{gymId}</c> segment reaches a tenant-scoped table yet
/// (the first is the Members increment), so every path through here either sets no tenant (no route
/// value) or sets one nothing reads. The whole existing suite continuing to pass is the assertion that
/// this stays true — see the four tests in <c>TenantResolutionTests</c>.</para>
/// <para><b>404, never 403, for a gym the caller has no grant for.</b> "You may not see this gym" and
/// "this gym does not exist" must be indistinguishable, or the status code becomes an oracle for
/// enumerating tenants. The response carries no body written here — the framework's status-code-pages
/// handler (already wired in <c>Program.cs</c> via <c>AddProblemDetails</c> +
/// <c>UseStatusCodePages()</c>) fills in the same RFC 7807 shape a "not found" from a handler would,
/// so a caller cannot tell the two apart from the response either.</para>
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
    /// An unauthenticated caller holds no grants. Every gym-routed endpoint today requires
    /// authentication at the policy level, so <c>UseAuthorization()</c> — which runs before this
    /// middleware — has already turned an anonymous request away with 401; this arm exists so that an
    /// anonymous gym-routed endpoint, were one ever added, fails closed here too instead of reading
    /// grants for a caller who has none.
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
