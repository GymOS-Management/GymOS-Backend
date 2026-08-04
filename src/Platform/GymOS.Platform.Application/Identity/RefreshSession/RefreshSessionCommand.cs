using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.RefreshSession;

/// <summary>
/// Exchange a refresh token for a new session, rotating the refresh token (AD-4).
/// The raw token arrives from the httpOnly cookie; the handler only ever hashes it.
/// </summary>
[NoPermissionRequired("Authorized by possession of a valid refresh token, not by a permission.")]
public sealed record RefreshSessionCommand(string RefreshToken, string IpAddress)
    : ICommand<Result<AuthenticatedSession>>;
