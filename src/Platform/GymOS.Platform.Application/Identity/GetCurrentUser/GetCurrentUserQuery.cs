using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Identity.GetCurrentUser;

/// <summary>
/// The signed-in user plus their effective permission set (AD-22 §6.3). This is the
/// single endpoint the frontend gates its UI from — one call, no guessing, and the
/// server still enforces every permission independently.
/// </summary>
public sealed record CurrentUser(UserProfile Profile, IReadOnlyCollection<string> Permissions);

[NoPermissionRequired("Returns only the caller's own identity and permission set.")]
public sealed record GetCurrentUserQuery(Guid UserId) : IQuery<Result<CurrentUser>>;
