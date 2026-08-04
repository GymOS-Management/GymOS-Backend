using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.SignIn;

/// <summary>
/// Authenticate with email and password (AD-4). The IP address is passed in from the
/// edge rather than read from ambient state — the handler stays a pure function of
/// its inputs and therefore testable without an HTTP context.
/// </summary>
[NoPermissionRequired("Sign-in is how a caller obtains permissions; requiring one would be circular.")]
public sealed record SignInCommand(string Email, string Password, string IpAddress)
    : ICommand<Result<AuthenticatedSession>>;
