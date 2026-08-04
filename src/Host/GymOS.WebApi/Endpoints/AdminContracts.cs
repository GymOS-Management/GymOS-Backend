namespace GymOS.WebApi.Endpoints;

/// <summary>
/// Request bodies for the platform-administration endpoints (AD-12: this is the shape the
/// generated clients are built from, so it is API surface).
/// </summary>
public sealed record InviteUserRequest(
    string Email,
    string FirstName,
    string LastName,
    IReadOnlyCollection<Guid>? RoleIds);

public sealed record UpdateUserRequest(string FirstName, string LastName);

/// <param name="Reason">Required and stored — a suspension nobody can explain later is a support ticket.</param>
public sealed record SuspendUserRequest(string Reason);

/// <param name="RoleIds">
/// The complete set the user should end up with, not a delta. Sending an empty array
/// removes every role.
/// </param>
public sealed record SetUserRolesRequest(IReadOnlyCollection<Guid> RoleIds);
