using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/settings</c> — the declared catalog (M7 spec §2-4): every setting's effective value,
/// where it came from, and a batch write per category. There is no per-key write; a category is the
/// unit of change because <see cref="ISettingsValidator"/> validates the would-be state of the whole
/// category's bound options, not one field at a time.
/// </summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Settings").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/settings")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Settings");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(PlatformPermissions.SettingsRead)
            .WithName("GetSettings")
            .WithSummary("The declared catalog, each key's effective value and where it came from")
            .WithDescription(
                "Every declared setting, optionally narrowed to one `category`. `source` says which "
                    + "layer answered: a stored row beats configuration, which beats the definition's "
                    + "own default. A sensitive key's value comes back as \"••••\" once a row exists "
                    + "for it — the plaintext is never returned by this endpoint. The Maintenance "
                    + "category is readable here but changes only through its own endpoint, not this "
                    + "one's PUT.")
            .Produces<IReadOnlyList<SettingView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/", PutAsync)
            .RequireAuthorization(PlatformPermissions.SettingsManage)
            .WithName("UpdateSettings")
            .WithSummary("Write one category's batch of settings")
            .WithDescription(
                "Every key in `values` must belong to `category`. An empty string or a null value "
                    + "deletes the row, returning that key to configuration or its own default; for a "
                    + "sensitive key, sending back \"••••\" leaves the stored value unchanged. The "
                    + "Maintenance category is refused here (400 `settings.use_maintenance_endpoint`) "
                    + "— it changes only through its own confirmed endpoint. The whole batch is "
                    + "checked before anything is written: a duplicate key, an unknown key, a key from "
                    + "another category, a value that fails its own rule, or a would-be state that "
                    + "fails cross-field validation all fail the entire batch with nothing changed. "
                    + "Returns the category's fresh views on success.")
            .Produces<IReadOnlyList<SettingView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/test-email", TestEmailAsync)
            .RequireAuthorization(PlatformPermissions.SettingsManage)
            .WithName("TestEmailConnection")
            .WithSummary("Test the SMTP connection — sends nothing")
            .WithDescription(
                "Connects with the effective Email settings, authenticates if a username is "
                    + "configured, and issues a NOOP: the same round trips a real send would open, "
                    + "none of the send itself (ADR-53 stands). A refused login or an unreachable "
                    + "host is still a 200 — `ok` is false and `stage` says how far the attempt got "
                    + "(`Connect`, `Tls`, or `Authenticate`). 400 `settings.email_disabled` when "
                    + "`Email:Enabled` is false; enable and save it first.")
            .Produces<SmtpConnectionTestResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/maintenance-mode", SetMaintenanceModeAsync)
            .RequireAuthorization(PlatformPermissions.SettingsManage)
            .RequiresConfirmAction()
            .WithName("SetMaintenanceMode")
            .WithSummary("Turn maintenance mode on or off")
            .WithDescription(
                "Requires the `X-Confirm-Action` header carrying the literal word `maintenance` "
                    + "(AD-6): while it is on, every caller but a SuperAdmin — holding `settings.manage` "
                    + "— is refused with 503, so a mis-clicked toggle must not be able to close the "
                    + "whole platform. `message` is shown to refused callers; omitting it falls back to "
                    + "the definition's own default. Returns the Maintenance category's fresh views.")
            .Produces<IReadOnlyList<SettingView>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        string? category = null)
    {
        SettingCategory? parsed = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (!TryParseCategory(category, out SettingCategory value))
            {
                return Result.Failure(SettingErrors.UnknownCategory(category)).ToProblem();
            }

            parsed = value;
        }

        Result<IReadOnlyList<SettingView>> result =
            await dispatcher.QueryAsync(new GetSettingsQuery(parsed), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> PutAsync(
        UpdateSettingsRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!TryParseCategory(request.Category, out SettingCategory category))
        {
            return Result.Failure(SettingErrors.UnknownCategory(request.Category)).ToProblem();
        }

        Result<IReadOnlyList<SettingView>> result = await dispatcher.SendAsync(
            new UpdateSettingsCommand(category, request.Values), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> TestEmailAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<SmtpConnectionTestResult> result =
            await dispatcher.SendAsync(new TestEmailConnectionCommand(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> SetMaintenanceModeAsync(
        SetMaintenanceModeRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<SettingView>> result = await dispatcher.SendAsync(
            new SetMaintenanceModeCommand(
                request.Enabled,
                request.Message,
                ConfirmationHeaders.ConfirmAction(httpContext.Request)),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    /// <summary>
    /// Against the names, not by number: <c>Enum.TryParse</c> alone accepts any numeric string
    /// ("999"), so <c>Enum.IsDefined</c> is required too.
    /// </summary>
    private static bool TryParseCategory(string value, out SettingCategory category) =>
        Enum.TryParse(value, ignoreCase: true, out category) && Enum.IsDefined(category);
}

/// <summary>The PUT body: a category and the batch of key/value writes for it (M7 spec §4).</summary>
public sealed record UpdateSettingsRequest(string Category, IReadOnlyList<SettingValueWrite> Values);

/// <summary>The maintenance-mode toggle's body (M7 spec §6). Confirmation travels as the
/// <c>X-Confirm-Action</c> header, not a body field, per AD-6.</summary>
public sealed record SetMaintenanceModeRequest(bool Enabled, string? Message);
