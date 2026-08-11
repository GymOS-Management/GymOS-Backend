using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Gyms;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/gyms</c> — gyms, which are also the platform's tenants (ARCHITECTURE.md §3.3).
///
/// Every route declares its permission (rule R11, deny by default). The declaration here is the first of
/// two enforcement layers; the same permission is declared on each command, so a route that lost its
/// policy still cannot reach a handler (AD-5).
/// </summary>
public static class GymEndpoints
{
    public static IEndpointRouteBuilder MapGymEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Gyms").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/gyms")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Gyms");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.GymsRead)
            .WithName("ListGyms")
            .WithSummary("List gyms")
            .WithDescription(
                "Paged and filterable. `search` matches name, slug, and owner email; `status` filters " +
                "on Provisioning, Active, Suspended, or Deleted; `country` filters on ISO alpha-2. " +
                "Deleted gyms are excluded unless `includeDeleted=true`, so a deleted tenant is never " +
                "counted in a figure someone reports upward. `pageSize` is capped server-side.")
            .Produces<PagedResult<GymListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{gymId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.GymsRead)
            .WithName("GetGym")
            .WithSummary("One gym, with its branding, subscription, and resolved features")
            .WithDescription(
                "A soft-deleted gym still resolves here, deliberately: an operator investigating why a " +
                "tenant stopped working needs to see that it was deleted and when. `features` carries " +
                "the whole catalogue resolved for this gym, each entry naming the layer that decided " +
                "it — the same body `GET /gyms/{gymId}/features` returns — but only when you hold " +
                "`features.read`; without it `features` is `null`, because one entry can carry " +
                "`overrideReason`, the commercially sensitive text behind a paid feature granted for " +
                "free. Device summary and aggregate stats arrive with the modules that own them " +
                "(M5–M6) rather than as fields that always read zero.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", ProvisionAsync)
            .RequireAuthorization(PlatformPermissions.GymsCreate)
            .WithName("ProvisionGym")
            .WithSummary("Provision a gym")
            .WithDescription(
                "The gym is created in Provisioning: set up — branding, staff — before members can " +
                "reach it, then `activate` when it opens. The slug is permanent and is never reused, " +
                "including by a deleted gym, because it appears in URLs members may still have. " +
                "Returns the created gym and its `ETag`, so no follow-up GET is needed.")
            .Produces<GymDetail>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{gymId:guid}", UpdateAsync)
            .RequireAuthorization(PlatformPermissions.GymsUpdate)
            .RequiresIfMatch()
            .WithName("UpdateGym")
            .WithSummary("Change a gym's details")
            .WithDescription(
                "Slug is not editable — it is the gym's public address and the confirmation phrase for " +
                "deletion. Requires `If-Match` carrying the `rowVersion` you read: without it the " +
                "request is refused with 428, and a stale version with 409. Returns the updated gym " +
                "and its new `ETag`.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{gymId:guid}/activate", ActivateAsync)
            .RequireAuthorization(PlatformPermissions.GymsUpdate)
            .HonoursIfMatch()
            .WithName("ActivateGym")
            .WithSummary("Open a provisioned gym for business")
            .WithDescription(
                "The deliberate moment a gym goes live. Only a gym in Provisioning can be activated: a " +
                "suspended gym goes through `reactivate`, which is a different decision and a different " +
                "permission. `If-Match` is honored when supplied but not required — the lifecycle " +
                "transition rules already refuse the unsafe orderings. Returns the updated gym.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{gymId:guid}/suspend", SuspendAsync)
            .RequireAuthorization(PlatformPermissions.GymsSuspend)
            .HonoursIfMatch()
            .WithName("SuspendGym")
            .WithSummary("Withhold service from a gym")
            .WithDescription(
                "Reversible. The reason is required and is returned by the API — an operator lifting " +
                "the suspension next week needs to know why it happened. `If-Match` is honored when " +
                "supplied but not required. Returns the updated gym.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{gymId:guid}/reactivate", ReactivateAsync)
            .RequireAuthorization(PlatformPermissions.GymsSuspend)
            .HonoursIfMatch()
            .WithName("ReactivateGym")
            .WithSummary("Lift a suspension")
            .WithDescription(
                "Returns the gym to Active and clears the suspension reason. Only a suspended gym can " +
                "be reactivated. `If-Match` is honored when supplied but not required. Returns the " +
                "updated gym.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{gymId:guid}", DeleteAsync)
            .RequireAuthorization(PlatformPermissions.GymsDelete)
            .RequiresConfirmAction()
            .HonoursIfMatch()
            .WithName("DeleteGym")
            .WithSummary("Soft-delete a gym")
            .WithDescription(
                "Requires the `X-Confirm-Action` header carrying the gym's slug (AD-6): this removes an " +
                "entire business's access, so a mis-clicked row in a list must not be able to do it. " +
                "The row survives the retention window so invoices and audit entries stay readable; a " +
                "purge job performs the hard delete. Terminal — there is no undelete. Repeating the " +
                "call on an already-deleted gym succeeds. `GET` the gym afterwards to see the recorded " +
                "deletion.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{gymId:guid}/branding", UpdateBrandingAsync)
            .RequireAuthorization(PlatformPermissions.GymsUpdate)
            .RequiresIfMatch()
            .WithName("UpdateGymBranding")
            .WithSummary("Replace a gym's branding")
            .WithDescription(
                "Declarative: send the complete branding, and an omitted value is cleared. A custom " +
                "domain must be unique across the platform. Requires `If-Match` carrying the gym's " +
                "`rowVersion` — branding is part of the gym's representation and shares its version, so " +
                "a successful branding change also moves the gym's `ETag`. Returns the updated gym.")
            .Produces<GymDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapGet("/{gymId:guid}/features", GetFeaturesAsync)
            .RequireAuthorization(PlatformPermissions.FeaturesRead)
            .WithName("GetGymFeatures")
            .WithSummary("What this gym's features resolve to, and why")
            .WithDescription(
                "The **whole catalogue**, each entry carrying `enabled` and the `source` that decided "
                    + "it — `Override`, `Plan`, or `Default` (§2.4). Not a list of exceptions: \"what "
                    + "can this gym do\" is the question worth answering, and `source` is what makes "
                    + "the answer explainable in a support conversation. `overrideReason` is present "
                    + "only when an override is what decided it. A gym with no live subscription "
                    + "resolves every feature against the platform default.")
            .Produces<IReadOnlyList<ResolvedFeatureDetail>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{gymId:guid}/features", SetFeaturesAsync)
            .RequireAuthorization(PlatformPermissions.FeaturesManage)
            .WithName("SetGymFeatures")
            .WithSummary("Grant or withdraw this gym's feature exceptions")
            .WithDescription(
                "An override is stored **only when it differs from what the gym would get anyway**. "
                    + "Ask for exactly what the plan — or the platform default, when the plan is "
                    + "silent — already gives, and any existing exception is **withdrawn** instead, so "
                    + "a gym that later changes plan follows the new plan rather than staying frozen "
                    + "by a row nobody remembers creating. `reason` is therefore required only when an "
                    + "override is actually needed.\n\n"
                    + "The response is the **full resolution, not an echo** of the request: after a "
                    + "withdrawal the stored state is deliberately not what was sent.\n\n"
                    + "Only the keys listed are touched. No `If-Match`: each override is its own "
                    + "record keyed on (gym, feature), so there is no single version to match, and two "
                    + "operators editing different features do not conflict. Deciding what a *plan* "
                    + "includes is `billing.manage` instead.")
            .Produces<IReadOnlyList<ResolvedFeatureDetail>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken,
        int? page = null,
        int? pageSize = null,
        string? sortBy = null,
        string? sortDir = null,
        string? search = null,
        string? status = null,
        string? country = null,
        bool includeDeleted = false)
    {
        Result<PagedResult<GymListItem>> result = await dispatcher.QueryAsync(
            new ListGymsQuery(
                new PageRequest(page, pageSize, sortBy, sortDir, search),
                status,
                country,
                includeDeleted),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid gymId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.QueryAsync(new GetGymQuery(gymId), cancellationToken);

        // This ETag is exactly what a client sends back as If-Match, so read and write share one version
        // token rather than two representations that could drift.
        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ProvisionAsync(
        ProvisionGymRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new ProvisionGymCommand(
                request.Slug,
                request.Name,
                request.OwnerName,
                request.OwnerEmail,
                request.Phone,
                request.CountryCode,
                request.TimeZoneId,
                request.Currency),
            cancellationToken);

        return WriteResults.Created(
            result,
            location: result.IsSuccess ? $"/api/v1/gyms/{result.Value.Id}" : string.Empty,
            rowVersion: result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> UpdateAsync(
        Guid gymId,
        UpdateGymRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new UpdateGymCommand(
                gymId,
                request.Name,
                request.OwnerName,
                request.OwnerEmail,
                request.Phone,
                request.CountryCode,
                request.TimeZoneId,
                request.Currency,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ActivateAsync(
        Guid gymId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new ActivateGymCommand(gymId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SuspendAsync(
        Guid gymId,
        SuspendGymRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new SuspendGymCommand(gymId, request.Reason, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ReactivateAsync(
        Guid gymId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new ReactivateGymCommand(gymId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> DeleteAsync(
        Guid gymId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(
            new DeleteGymCommand(
                gymId,
                ConfirmationHeaders.ConfirmAction(httpContext.Request),
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.NoContent(result);
    }

    private static async Task<IResult> UpdateBrandingAsync(
        Guid gymId,
        UpdateGymBrandingRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<GymDetail> result = await dispatcher.SendAsync(
            new UpdateGymBrandingCommand(
                gymId,
                request.LogoUrl,
                request.FaviconUrl,
                request.PrimaryColour,
                request.SecondaryColour,
                request.CustomDomain,
                request.EmailSenderName,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> GetFeaturesAsync(
        Guid gymId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await dispatcher.QueryAsync(
            new GetGymFeaturesQuery(gymId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> SetFeaturesAsync(
        Guid gymId,
        SetGymFeaturesRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ResolvedFeatureDetail>> result = await dispatcher.SendAsync(
            new SetGymFeaturesCommand(gymId, request.Features ?? []), cancellationToken);

        // No ETag: the resolution is not a versioned resource, and there is no If-Match to echo
        // (see the route's description).
        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
