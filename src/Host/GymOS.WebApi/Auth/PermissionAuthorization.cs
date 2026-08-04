using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace GymOS.WebApi.Auth;

/// <summary>
/// Permission-based authorization at the endpoint (AD-5, first enforcement layer).
///
/// Policies are produced on demand from the permission name rather than registered
/// one-by-one at startup: permissions are module-declared data (AD-22), so a static
/// registration list would have to be edited every time a module declares one — and the
/// endpoint would fail at runtime with "policy not found" if anyone forgot.
/// </summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        Permission = permission;
    }

    public string Permission { get; }
}

/// <summary>
/// Builds a policy for any <c>RequireAuthorization("some.permission")</c> name, falling
/// back to the default provider for the framework's own policies.
/// </summary>
public sealed class PermissionPolicyProvider : IAuthorizationPolicyProvider
{
    /// <summary>
    /// Only names shaped like a permission key (<c>module.action</c>) become permission
    /// policies. Anything else falls through, so a genuine typo surfaces as a missing
    /// policy instead of a requirement nobody can ever satisfy.
    /// </summary>
    private const char PermissionSeparator = '.';

    private readonly DefaultAuthorizationPolicyProvider _fallback;

    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fallback = new DefaultAuthorizationPolicyProvider(options);
    }

    public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

    public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

    public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        if (!policyName.Contains(PermissionSeparator, StringComparison.Ordinal))
        {
            return _fallback.GetPolicyAsync(policyName);
        }

        AuthorizationPolicy policy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();

        return Task.FromResult<AuthorizationPolicy?>(policy);
    }
}

/// <summary>
/// Evaluates the requirement through the same <see cref="IPermissionEvaluator"/> the
/// handler pipeline uses, so the two layers cannot disagree about what a permission grants.
/// </summary>
public sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public PermissionAuthorizationHandler(
        IPermissionEvaluator permissionEvaluator,
        IHttpContextAccessor httpContextAccessor)
    {
        _permissionEvaluator = permissionEvaluator;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        // The framework's authorization API gives the handler no CancellationToken, so it comes
        // from the request instead: an abandoned request should stop its permission query rather
        // than hold a connection for a client that has already gone.
        CancellationToken cancellationToken =
            _httpContextAccessor.HttpContext?.RequestAborted ?? CancellationToken.None;

        if (await _permissionEvaluator.HasPermissionAsync(requirement.Permission, cancellationToken)
            .ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }

        // No explicit Fail(): leaving the requirement unmet is enough, and failing outright
        // would prevent any other handler from satisfying it — which matters the moment a
        // second grant path (delegation, break-glass) is added.
    }
}
