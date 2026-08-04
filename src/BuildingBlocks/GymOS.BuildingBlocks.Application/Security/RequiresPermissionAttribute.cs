namespace GymOS.BuildingBlocks.Application.Security;

/// <summary>
/// The permission a command or query requires (AD-5). Declared on the request type, not
/// on the handler, so the requirement travels with the contract: any endpoint, job, or
/// automation rule that dispatches this request is checked, not just the one the author
/// had in mind.
///
/// This is the second of the two enforcing layers. The endpoint policy rejects
/// unauthorized callers before dispatch; this catches anything that reached a handler
/// anyway — a route that forgot its policy, or an internal caller taking a shortcut.
/// Belt and braces, because an unprotected write endpoint is not a bug you find in
/// testing (AD-5: "no handler is reachable without its permission even if an endpoint is
/// misconfigured").
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequiresPermissionAttribute : Attribute
{
    public RequiresPermissionAttribute(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>
/// Marks a request as reachable without any permission — sign-in, refresh, and the
/// self-service endpoints where holding a valid token *is* the authorization.
///
/// It exists so that "no permission needed" is a statement someone made on purpose. A
/// request carrying neither this nor <see cref="RequiresPermissionAttribute"/> is an
/// omission, and the convention test treats it as one (rule R11, deny by default).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class NoPermissionRequiredAttribute : Attribute
{
    public NoPermissionRequiredAttribute(string justification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        Justification = justification;
    }

    /// <summary>Why this request needs no permission. Required — an unexplained exemption is a smell.</summary>
    public string Justification { get; }
}
