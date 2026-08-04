using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/users</c> — platform administrators (ARCHITECTURE.md §3.6).
///
/// Every route declares its permission (rule R11, deny by default). The declaration here is
/// the first of two enforcement layers; the same permission is declared on each command, so
/// a route that lost its policy still cannot reach a handler (AD-5).
/// </summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Platform users").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/users")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Platform users");

        group.MapGet("/", ListAsync)
            .RequireAuthorization(PlatformPermissions.UsersRead)
            .WithName("ListUsers")
            .WithSummary("List platform administrators")
            .WithDescription(
                "Paged and filterable. `search` matches email, first name, and last name; " +
                "`status` filters on Active, Suspended, or PendingActivation. `pageSize` is " +
                "capped server-side.")
            .Produces<PagedResult<UserListItem>>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/{userId:guid}", GetAsync)
            .RequireAuthorization(PlatformPermissions.UsersRead)
            .WithName("GetUser")
            .WithSummary("One administrator, with roles and resolved permissions")
            .WithDescription("Returns the effective permission set alongside the roles that grant it.")
            .Produces<UserDetail>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", InviteAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("InviteUser")
            .WithSummary("Create an administrator account")
            .WithDescription(
                "The account is created without a password, in PendingActivation, and cannot " +
                "sign in until its owner sets one. No administrator ever sets another's password.")
            .Produces<CreatedResourceResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{userId:guid}", UpdateAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("UpdateUser")
            .WithSummary("Change an administrator's name")
            .WithDescription("Email is not editable here — it is the login identity and the audit key.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{userId:guid}/suspend", SuspendAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("SuspendUser")
            .WithSummary("Suspend an administrator and end their sessions")
            .WithDescription(
                "Rotates the security stamp and revokes every refresh token, so access ends " +
                "immediately rather than at token expiry. Refused for your own account, and " +
                "for the last active SuperAdmin.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:guid}/reactivate", ReactivateAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("ReactivateUser")
            .WithSummary("Lift a suspension")
            .WithDescription(
                "An account that never set a password returns to PendingActivation rather " +
                "than becoming usable.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{userId:guid}/roles", SetRolesAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("SetUserRoles")
            .WithSummary("Replace an administrator's roles")
            .WithDescription(
                "Declarative: send the complete set. Refused for your own account, and refused " +
                "when it would remove the role from the last active SuperAdmin.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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
        string? status = null)
    {
        Result<PagedResult<UserListItem>> result = await dispatcher.QueryAsync(
            new ListUsersQuery(new PageRequest(page, pageSize, sortBy, sortDir, search), status),
            cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetAsync(
        Guid userId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<UserDetail> result = await dispatcher.QueryAsync(new GetUserQuery(userId), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> InviteAsync(
        InviteUserRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<Guid> result = await dispatcher.SendAsync(
            new InviteUserCommand(request.Email, request.FirstName, request.LastName, request.RoleIds ?? []),
            cancellationToken);

        return result.IsSuccess
            ? TypedResults.Created($"/api/v1/users/{result.Value}", new CreatedResourceResponse(result.Value))
            : result.ToProblem();
    }

    private static async Task<IResult> UpdateAsync(
        Guid userId,
        UpdateUserRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(
            new UpdateUserCommand(userId, request.FirstName, request.LastName),
            cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    private static async Task<IResult> SuspendAsync(
        Guid userId,
        SuspendUserRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(
            new SuspendUserCommand(userId, request.Reason),
            cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    private static async Task<IResult> ReactivateAsync(
        Guid userId,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(new ReactivateUserCommand(userId), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    private static async Task<IResult> SetRolesAsync(
        Guid userId,
        SetUserRolesRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(
            new SetUserRolesCommand(userId, request.RoleIds ?? []),
            cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }
}
