using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.SignOut;

/// <summary>
/// Revoke the current session's refresh token. <paramref name="UserId"/> comes from
/// the caller's access token: a session can only be ended by the user who owns it.
/// </summary>
[NoPermissionRequired("Ends the caller's own session; a valid token is the authorization.")]
public sealed record SignOutCommand(Guid UserId, string? RefreshToken, string IpAddress)
    : ICommand<Result>;
