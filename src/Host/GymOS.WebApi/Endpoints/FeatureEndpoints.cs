using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Features;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/features</c> — the feature catalogue (ARCHITECTURE.md §3.5).
///
/// <para>There is deliberately no <c>POST</c> and no <c>DELETE</c>: a feature key is a constant that
/// code branches on, so one invented at runtime would be a flag nothing reads, and a deleted one
/// would strand the code reading it. The catalogue grows in a reviewed commit
/// (<c>PlatformFeatures</c>) and is inserted at startup.</para>
/// </summary>
public static class FeatureEndpoints
{
    public static IEndpointRouteBuilder MapFeatureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Features").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/features")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Features");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.FeaturesRead)
            .WithName("ListFeatures")
            .WithSummary("The feature catalogue")
            .WithDescription(
                "Every feature the platform can switch on, with `adoptionCount` — how many gyms "
                    + "currently resolve it **on** through the full three-layer rule (override → "
                    + "plan → platform default), not merely how many plans include it. "
                    + "`overrideCount` is how many of those gyms hold an explicit exception. Both "
                    + "counts exclude deleted gyms, so they describe one population. Ordered by "
                    + "category then name; the catalogue is seeded, so this list is not paged.")
            .Produces<IReadOnlyList<FeatureListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPut("/{featureId:guid}", UpdateAsync)
            .RequireAuthorization(PlatformPermissions.FeaturesManage)
            .RequiresIfMatch()
            .WithName("UpdateFeature")
            .WithSummary("Edit a feature's copy, its platform default, and whether it is sold")
            .WithDescription(
                "The **key is not editable** — code branches on it, and it is what the API returns. "
                    + "`If-Match` is **required**: this overwrites what you read, and changing "
                    + "`defaultEnabled` re-answers the feature for every gym that has neither an "
                    + "override nor a plan opinion — the widest blast radius of any write here. "
                    + "`isPremium` is informational: what a gym gets is decided by its plan and its "
                    + "overrides.")
            .Produces<FeatureListItem>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<FeatureListItem>> result = await dispatcher.QueryAsync(
            new ListFeaturesQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> UpdateAsync(
        Guid featureId,
        UpdateFeatureRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<FeatureListItem> result = await dispatcher.SendAsync(
            new UpdateFeatureCommand(
                featureId,
                request.Name,
                request.Description,
                request.Category,
                request.DefaultEnabled,
                request.IsPremium,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }
}
