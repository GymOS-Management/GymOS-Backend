using System.Globalization;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Identity;

namespace GymOS.WebApi.Middleware;

/// <summary>
/// Closes the platform to everyone but a SuperAdmin while maintenance mode is on (M7 spec §6).
///
/// <para>Sits in <c>Program.cs</c> right after <see cref="TenantResolutionMiddleware"/>, which itself
/// runs after authentication and rate limiting, and before <c>UseAuthorization</c>: authentication has
/// already populated <see cref="HttpContext.User"/> by the time this runs, which is what lets
/// <see cref="IPermissionEvaluator"/> resolve who the caller is, but no route's authorization policy has
/// been enforced yet, so a refused caller reaches no endpoint handler.</para>
///
/// <para><see cref="IPermissionEvaluator"/> is scoped and DB-backed with a per-request snapshot cache
/// (<c>CallerSnapshot</c>), so the permission check costs one query per request — and only while
/// maintenance is on and only for a caller authentication has identified; an anonymous caller's
/// snapshot lookup short-circuits to an empty set with no query at all.</para>
///
/// <para>Signing out must always be possible: a refused caller's only way out of maintenance is
/// to end their session and sign in as someone who passes, so <c>POST /api/v1/auth/logout</c> is
/// exempt alongside login and refresh.</para>
/// </summary>
public sealed class MaintenanceModeMiddleware
{
    public const int RetryAfterSeconds = 300;

    private static readonly PathString[] AlwaysOpen = ["/health", "/version"];

    private readonly RequestDelegate _next;

    public MaintenanceModeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ISettings<MaintenanceOptions> maintenance, IPermissionEvaluator permissions)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(permissions);

        MaintenanceOptions current = maintenance.Current;
        if (!current.Enabled
            || IsAlwaysOpen(context.Request)
            || await permissions.HasPermissionAsync(PlatformPermissions.SettingsManage, context.RequestAborted))
        {
            await _next(context);
            return;
        }

        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await Results.Problem(
            title: "Maintenance in progress",
            detail: current.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "settings.maintenance",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "settings.maintenance" })
            .ExecuteAsync(context);
    }

    private static bool IsAlwaysOpen(HttpRequest request) =>
        AlwaysOpen.Any(prefix => request.Path.StartsWithSegments(prefix))
        || (HttpMethods.IsPost(request.Method)
            && (request.Path.Equals("/api/v1/auth/login", StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals("/api/v1/auth/refresh", StringComparison.OrdinalIgnoreCase)
                || request.Path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase)));
}
