using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Roles;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/roles</c> — read-only in M1.3.
///
/// Deliberately read-only: the seeded platform roles are code-canonical (the startup sync
/// reconciles their grants back to what the code declares), so an endpoint that edited them
/// would be overwritten on the next deployment. Custom, editable roles belong to tenant
/// scope and arrive with the gym module.
/// </summary>
public static class RoleEndpoints
{
    public static IEndpointRouteBuilder MapRoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Roles").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/roles")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Roles");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.RolesRead)
            .WithName("ListRoles")
            .WithSummary("List platform roles")
            .WithDescription(
                "Each role reports the permissions it bundles and how many administrators hold " +
                "it — the number to know before changing what a role grants.")
            .Produces<IReadOnlyList<RoleDetail>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{roleId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.RolesRead)
            .WithName("GetRole")
            .WithSummary("One role and its permission grants")
            .WithDescription(
                "System roles are seeded and their grants are reconciled to the code on every " +
                "startup, so they are not editable through the API.")
            .Produces<RoleDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(IDispatcher dispatcher, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<RoleDetail>> result =
            await dispatcher.QueryAsync(new ListRolesQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid roleId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<RoleDetail> result = await dispatcher.QueryAsync(new GetRoleQuery(roleId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
