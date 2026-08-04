namespace GymOS.BuildingBlocks.Application.Security;

/// <summary>
/// Thrown when a request reached its handler without the permission it declares.
///
/// An exception rather than a failed <c>Result</c> on purpose. Business failures are
/// values in this codebase, but this is not a business outcome — by the time the pipeline
/// runs, the endpoint policy should already have refused the caller, so arriving here
/// means the route is misconfigured. Making it an exception keeps the defect loud
/// (logged, traced, alertable) instead of laundering it into a response the caller could
/// mistake for normal, and it avoids constructing a failed <c>Result&lt;T&gt;</c>
/// reflectively for an arbitrary response type. The host still answers 403 — the caller
/// sees the correct outcome either way.
/// </summary>
public sealed class PermissionDeniedException : Exception
{
    public PermissionDeniedException(string requestName, string permission)
        : base($"'{requestName}' requires permission '{permission}', which the caller does not hold.")
    {
        RequestName = requestName;
        Permission = permission;
    }

    public PermissionDeniedException()
    {
    }

    public PermissionDeniedException(string message)
        : base(message)
    {
    }

    public PermissionDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public string? RequestName { get; }

    public string? Permission { get; }
}
