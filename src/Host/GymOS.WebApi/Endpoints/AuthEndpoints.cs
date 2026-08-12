using System.Security.Claims;
using Asp.Versioning;
using Asp.Versioning.Builder;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Activation;
using GymOS.Platform.Application.Identity.ChangePassword;
using GymOS.Platform.Application.Identity.GetCurrentUser;
using GymOS.Platform.Application.Identity.RefreshSession;
using GymOS.Platform.Application.Identity.SignIn;
using GymOS.Platform.Application.Identity.SignOut;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.SharedKernel.Results;
using GymOS.WebApi.Auth;
using GymOS.WebApi.Extensions;

namespace GymOS.WebApi.Endpoints;

/// <summary>
/// <c>/api/v1/auth</c> (ARCHITECTURE.md §3.1). Endpoints do three things and nothing
/// more: translate HTTP into a command, dispatch it, translate the result back. All
/// authentication logic lives in handlers, which is what makes it testable without a
/// web server and reusable by a future v2 route (AD-2/AD-37).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        ApiVersionSet versionSet = endpoints.NewApiVersionSet("Authentication").Build();

        RouteGroupBuilder group = endpoints
            .MapGroup("/api/v{version:apiVersion}/auth")
            .WithApiVersionSet(versionSet)
            .HasApiVersion(new ApiVersion(1, 0))
            .WithTags("Authentication");

        group.MapPost("/login", SignInAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.SignInPolicy)
            .WithName("SignIn")
            .WithSummary("Sign in with email and password")
            .WithDescription(
                "Returns a short-lived access token in the body and sets the rotating refresh " +
                "token as an httpOnly cookie. Rate-limited per client IP; repeated failures " +
                "lock the account for the configured lockout period.")
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("RefreshSession")
            .WithSummary("Exchange the refresh cookie for a new access token")
            .WithDescription(
                "Rotates the refresh token: the presented token is revoked and replaced. " +
                "Presenting an already-rotated token is treated as a leak and revokes the " +
                "rest of that rotation chain.")
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        // The three below require authentication but no specific permission: they act on
        // the caller's own account, so holding a valid token *is* the authorization.
        // Permission-gated endpoints arrive with the users/roles surface (M1.3).
        group.MapPost("/logout", SignOutAsync)
            .RequireAuthorization()
            .WithName("SignOut")
            .WithSummary("Revoke the current session")
            .WithDescription("Idempotent: succeeds whether or not a valid refresh cookie was presented.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("/me", GetCurrentUserAsync)
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .WithSummary("The signed-in user and their effective permissions")
            .WithDescription(
                "The single source the frontend gates its UI from. Permissions here are " +
                "advisory for display only — the API enforces them independently on every call.")
            .Produces<CurrentUserResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/change-password", ChangePasswordAsync)
            .RequireAuthorization()
            // The same per-IP limiter sign-in uses: this endpoint verifies a password, so it is a
            // password oracle to anyone holding a stolen access token. The account lockout behind
            // it caps attempts per account; this caps them per source.
            .RequireRateLimiting(RateLimitingExtensions.SignInPolicy)
            .WithName("ChangePassword")
            .WithSummary("Change your own password")
            .WithDescription(
                "Requires the current password. Rotates the security stamp and revokes every " +
                "refresh token, including the caller's — the client must sign in again. " +
                "Wrong current passwords count towards the account lockout and are audited, " +
                "and the endpoint is rate-limited per client IP.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // ── Activation (ADR-53) ────────────────────────────────────────────────────────────────
        // Anonymous of necessity: the person following an invitation link has no way to sign in,
        // which is the situation these exist to resolve. The one-time token is the credential.

        group.MapGet("/set-password", CheckActivationTokenAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.SignInPolicy)
            .WithName("CheckActivationLink")
            .WithSummary("Is this activation link still usable?")
            .WithDescription(
                "Lets the console show \"this link has expired, ask for another\" instead of a form " +
                "that fails on submit. Answers **validity and nothing else** — no email, no name, no " +
                "roles — so holding a token reveals nothing about the account behind it. Every " +
                "failure (unknown, expired, already used, account gone or suspended) returns the " +
                "same `404` with the same `auth.link_not_usable` body, deliberately: a distinct " +
                "\"expired\" would confirm the token had once been real.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("/set-password", SetPasswordAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitingExtensions.SignInPolicy)
            .WithName("SetPassword")
            .WithSummary("Set a password from an invitation or reset link")
            .WithDescription(
                "Consumes the link — it works once, and is spent even when the password is rejected " +
                "by policy, so a refused attempt cannot leave a replayable link behind. Activates a " +
                "PendingActivation account, rotates the security stamp, and revokes every existing " +
                "refresh token: a reset ends any session that was already open, which is the point " +
                "when the reason for resetting is that somebody else is in.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static async Task<IResult> CheckActivationTokenAsync(
        string? token,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.QueryAsync(
            new CheckActivationTokenQuery(token ?? string.Empty), cancellationToken);

        return WriteResults.NoContent(result);
    }

    private static async Task<IResult> SetPasswordAsync(
        SetPasswordRequest request,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        Result result = await dispatcher.SendAsync(
            new SetPasswordCommand(request.Token, request.NewPassword), cancellationToken);

        return WriteResults.NoContent(result);
    }

    private static async Task<IResult> SignInAsync(
        SignInRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        IClock clock,
        CancellationToken cancellationToken)
    {
        Result<AuthenticatedSession> result = await dispatcher.SendAsync(
            new SignInCommand(request.Email, request.Password, ClientIpOf(httpContext)),
            cancellationToken);

        return CompleteSession(result, httpContext, clock);
    }

    private static async Task<IResult> RefreshAsync(
        HttpContext httpContext,
        IDispatcher dispatcher,
        IClock clock,
        CancellationToken cancellationToken)
    {
        string? presentedToken = RefreshTokenCookie.Read(httpContext.Request);

        Result<AuthenticatedSession> result = await dispatcher.SendAsync(
            new RefreshSessionCommand(presentedToken ?? string.Empty, ClientIpOf(httpContext)),
            cancellationToken);

        if (result.IsFailure)
        {
            // The session is over either way — clearing the cookie stops the client from
            // retrying a token that will never work again.
            RefreshTokenCookie.Clear(httpContext.Response);
        }

        return CompleteSession(result, httpContext, clock);
    }

    private static async Task<IResult> SignOutAsync(
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(httpContext.User, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        Result result = await dispatcher.SendAsync(
            new SignOutCommand(userId, RefreshTokenCookie.Read(httpContext.Request), ClientIpOf(httpContext)),
            cancellationToken);

        RefreshTokenCookie.Clear(httpContext.Response);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    private static async Task<IResult> GetCurrentUserAsync(
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(httpContext.User, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        Result<CurrentUser> result = await dispatcher.QueryAsync(
            new GetCurrentUserQuery(userId),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        CurrentUser current = result.Value;
        return TypedResults.Ok(new CurrentUserResponse(
            current.Profile.Id,
            current.Profile.Email,
            current.Profile.FirstName,
            current.Profile.LastName,
            current.Profile.Status,
            current.Profile.Roles,
            current.Permissions));
    }

    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        HttpContext httpContext,
        IDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(httpContext.User, out Guid userId))
        {
            return TypedResults.Unauthorized();
        }

        Result result = await dispatcher.SendAsync(
            new ChangePasswordCommand(
                userId,
                request.CurrentPassword,
                request.NewPassword,
                ClientIpOf(httpContext)),
            cancellationToken);

        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // Every session just died, this one included; leaving the cookie behind would
        // only produce a confusing 401 on the client's next refresh.
        RefreshTokenCookie.Clear(httpContext.Response);
        return TypedResults.NoContent();
    }

    private static IResult CompleteSession(
        Result<AuthenticatedSession> result,
        HttpContext httpContext,
        IClock clock)
    {
        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        AuthenticatedSession session = result.Value;
        RefreshTokenCookie.Write(httpContext.Response, session.RefreshToken, session.RefreshTokenExpiresAtUtc);

        return TypedResults.Ok(AccessTokenResponse.From(session, clock.UtcNow));
    }

    /// <summary>
    /// The caller's IP, recorded on refresh tokens for the audit trail. Never trusted
    /// for authorization — <c>X-Forwarded-For</c> handling belongs to the proxy
    /// configuration, not to a claim about identity.
    /// </summary>
    private static string ClientIpOf(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue(GymOSClaims.Subject), out userId) && userId != Guid.Empty;
}
