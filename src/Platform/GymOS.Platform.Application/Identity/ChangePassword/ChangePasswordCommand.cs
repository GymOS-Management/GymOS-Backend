using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.ChangePassword;

/// <summary>
/// Change one's own password. Requires the current password (ARCHITECTURE.md §3.1) —
/// a stolen access token alone must not be enough to take over the account
/// permanently.
/// </summary>
[NoPermissionRequired("Acts on the caller's own credential and re-verifies the current password.")]
public sealed record ChangePasswordCommand(
    Guid UserId,
    string CurrentPassword,
    string NewPassword,
    string IpAddress) : ICommand<Result>;
