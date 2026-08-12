using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Identity.Activation;
using GymOS.Platform.Application.Identity.Users;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Extensions;

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
    /// <summary>
    /// What became of the invitation or reset message: <c>sent</c>, <c>failed</c>, or
    /// <c>disabled</c>.
    ///
    /// <para><c>sent</c> means the mail server accepted it. <b>Not that it arrived</b> — a bounce, a
    /// spam filing, and a wrong-but-well-formed address are indistinguishable from here.</para>
    /// </summary>
    internal const string InvitationEmailHeader = "X-Invitation-Email";

    internal const string EmailSentValue = "sent";
    internal const string EmailFailedValue = "failed";

    /// <summary>
    /// <c>Email:Enabled</c> is false, so nothing was attempted and no link exists to follow.
    ///
    /// <para>Reported separately because it used to report as <c>sent</c>: the account was created,
    /// the caller was told the invitation had gone, and nobody could ever activate it. "Switched off"
    /// and "handed to a mail server" are different answers to the only question this header asks.</para>
    /// </summary>
    internal const string EmailDisabledValue = "disabled";

    private static string HeaderValueFor(EmailDispatch dispatch) => dispatch switch
    {
        EmailDispatch.Sent => EmailSentValue,
        EmailDispatch.Failed => EmailFailedValue,
        EmailDispatch.Suppressed => EmailDisabledValue,
        _ => EmailFailedValue,
    };

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
                "sign in until its owner sets one. No administrator ever sets another's password. " +
                "Returns the created administrator and its `ETag`, so no follow-up GET is needed.")
            .Produces<UserDetail>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{userId:guid}", UpdateAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .RequiresIfMatch()
            .WithName("UpdateUser")
            .WithSummary("Change an administrator's name")
            .WithDescription(
                "Email is not editable here — it is the login identity and the audit key. " +
                "Requires `If-Match` carrying the `rowVersion` you read: without it the request " +
                "is refused with 428, and a stale version with 409. Returns the updated " +
                "administrator and its new `ETag`.")
            .Produces<UserDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

        group.MapPost("/{userId:guid}/suspend", SuspendAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .HonoursIfMatch()
            .WithName("SuspendUser")
            .WithSummary("Suspend an administrator and end their sessions")
            .WithDescription(
                "Rotates the security stamp and revokes every refresh token, so access ends " +
                "immediately rather than at token expiry. Refused for your own account, and " +
                "for the last active SuperAdmin. `If-Match` is honored when supplied but not " +
                "required — the lifecycle transition rules already refuse the unsafe orderings. " +
                "Returns the updated administrator.")
            .Produces<UserDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:guid}/reactivate", ReactivateAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .HonoursIfMatch()
            .WithName("ReactivateUser")
            .WithSummary("Lift a suspension")
            .WithDescription(
                "An account that never set a password returns to PendingActivation rather " +
                "than becoming usable. `If-Match` is honored when supplied but not required. " +
                "Returns the updated administrator.")
            .Produces<UserDetail>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:guid}/resend-invitation", ResendInvitationAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("ResendInvitation")
            .WithSummary("Send the activation link again")
            .WithDescription(
                "For an administrator who has not activated yet. Issuing a new link invalidates the " +
                "previous one, so at most one is ever live. Refused with `users.not_pending_activation` " +
                "for an account that has already activated — that case wants a password reset instead. " +
                "`X-Invitation-Email` reports `sent`, `failed`, or `disabled` (email is switched " +
                "off server-side, so no link exists to follow); `sent` means the mail server " +
                "accepted it and never " +
                "means delivered.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{userId:guid}/reset-password", SendPasswordResetAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .WithName("SendPasswordReset")
            .WithSummary("Email a password-reset link")
            .WithDescription(
                "Sends the account's owner a one-time link. **It does not set a password** — an " +
                "administrator who could set another administrator's password would hold a credential " +
                "that is no longer one (§3.6). Refused for an account that has never activated " +
                "(re-send the invitation instead) and for a suspended one (reactivate first).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPut("/{userId:guid}/roles", SetRolesAsync)
            .RequireAuthorization(PlatformPermissions.UsersManage)
            .RequiresIfMatch()
            .WithName("SetUserRoles")
            .WithSummary("Replace an administrator's roles")
            .WithDescription(
                "Declarative: send the complete set. Refused for your own account, and refused " +
                "when it would remove the role from the last active SuperAdmin. Requires " +
                "`If-Match` carrying the `rowVersion` you read. Returns the updated administrator.")
            .Produces<UserDetail>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired);

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

        // This ETag is exactly what a client sends back as If-Match, so read and write share one
        // version token rather than two representations that could drift.
        return WriteResults.Read(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    /// <summary>
    /// The response body is unchanged — the created administrator, exactly as before. Whether the
    /// invitation reached the mail server rides in <c>X-Invitation-Email</c> instead, the shape
    /// <c>Idempotency-Replayed</c> already established: the account is created either way (ADR-53),
    /// and the console needs to know whether to say "invitation sent" or "created, but the email did
    /// not go — re-send it".
    /// </summary>
    private static async Task<IResult> InviteAsync(
        InviteUserRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<InvitedUser> result = await dispatcher.SendAsync(
            new InviteUserCommand(request.Email, request.FirstName, request.LastName, request.RoleIds ?? []),
            cancellationToken);

        if (result.IsSuccess)
        {
            httpContext.Response.Headers[InvitationEmailHeader] =
                HeaderValueFor(result.Value.Email);
        }

        Result<UserDetail> user = result.IsSuccess
            ? Result.Success(result.Value.User)
            : Result.Failure<UserDetail>(result.Error!);

        return WriteResults.Created(
            user,
            location: user.IsSuccess ? $"/api/v1/users/{user.Value.Id}" : string.Empty,
            rowVersion: user.IsSuccess ? user.Value.RowVersion : null);
    }

    private static async Task<IResult> ResendInvitationAsync(
        Guid userId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<ActivationDispatch> result = await dispatcher.SendAsync(
            new ResendInvitationCommand(userId), cancellationToken);

        if (result.IsSuccess)
        {
            httpContext.Response.Headers[InvitationEmailHeader] =
                HeaderValueFor(result.Value.Email);
        }

        return WriteResults.NoContent(result);
    }

    private static async Task<IResult> SendPasswordResetAsync(
        Guid userId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<ActivationDispatch> result = await dispatcher.SendAsync(
            new SendPasswordResetCommand(userId), cancellationToken);

        if (result.IsSuccess)
        {
            httpContext.Response.Headers[InvitationEmailHeader] =
                HeaderValueFor(result.Value.Email);
        }

        return WriteResults.NoContent(result);
    }

    private static async Task<IResult> UpdateAsync(
        Guid userId,
        UpdateUserRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<UserDetail> result = await dispatcher.SendAsync(
            new UpdateUserCommand(
                userId,
                request.FirstName,
                request.LastName,
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SuspendAsync(
        Guid userId,
        SuspendUserRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<UserDetail> result = await dispatcher.SendAsync(
            new SuspendUserCommand(userId, request.Reason, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> ReactivateAsync(
        Guid userId,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<UserDetail> result = await dispatcher.SendAsync(
            new ReactivateUserCommand(userId, PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }

    private static async Task<IResult> SetRolesAsync(
        Guid userId,
        SetUserRolesRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result<UserDetail> result = await dispatcher.SendAsync(
            new SetUserRolesCommand(
                userId,
                request.RoleIds ?? [],
                PreconditionHeaders.IfMatch(httpContext.Request)),
            cancellationToken);

        return WriteResults.Updated(result, result.IsSuccess ? result.Value.RowVersion : null);
    }
}
