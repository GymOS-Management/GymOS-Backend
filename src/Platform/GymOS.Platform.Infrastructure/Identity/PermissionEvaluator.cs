using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Resolves the caller's permissions from storage (AD-22 §6.2, first term of the
/// effective-access formula) and answers both enforcement layers from the same snapshot.
///
/// The set is resolved <em>for the tenant the request acts for</em> (grants spec D3): platform
/// grants on a platform route, platform grants plus the grants naming that gym on a gym route.
/// A grant scoped to one gym therefore confers nothing anywhere else.
///
/// Reading from the database rather than from token claims is a deliberate trade: it costs
/// a query, and it means a revoked role stops working on the caller's *next request*
/// instead of whenever their access token happens to expire. For an administrative console
/// where revocation usually follows an incident, that is the correct side of the trade.
/// The cost is paid once per (caller, tenant), not once per check (see <see cref="CallerSnapshot"/>).
/// </summary>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    private readonly ICurrentUser _currentUser;
    private readonly ITenantContext _tenantContext;
    private readonly ICallerSnapshot _snapshot;

    public PermissionEvaluator(ICurrentUser currentUser, ITenantContext tenantContext, ICallerSnapshot snapshot)
    {
        _currentUser = currentUser;
        _tenantContext = tenantContext;
        _snapshot = snapshot;
    }

    public async Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        IReadOnlyCollection<string> held = await GetPermissionsAsync(cancellationToken);
        return held.Contains(permission, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyCollection<string>> GetPermissionsAsync(CancellationToken cancellationToken)
    {
        if (_currentUser.UserId is not { } userId)
        {
            return [];
        }

        // The acting tenant is whatever TenantResolutionMiddleware set — null on every platform route.
        UserSecuritySnapshot? snapshot = await _snapshot.GetAsync(userId, _tenantContext.TenantId, cancellationToken);

        // A suspended account holds nothing, whatever its roles say and whichever gym it acts for.
        // Status is re-read per request, so a suspension takes effect immediately rather than at
        // token expiry.
        return snapshot is null || !string.Equals(snapshot.Status, "Active", StringComparison.Ordinal)
            ? []
            : snapshot.Permissions;
    }
}

/// <summary>
/// Memoizes the caller's security reads for the lifetime of one request.
///
/// Without this, a single request would re-read the same rows several times: the token
/// validator checks the security stamp, the endpoint policy checks a permission, and the
/// pipeline behavior checks it again. The two reads are memoized separately — identity per
/// user, permissions per (user, tenant) — so the stamp check, which runs during authentication
/// and knows no tenant yet, never pays for a permission set it does not use. Scoped, so the
/// cache cannot outlive the request and serve a stale answer to the next one — and instance
/// state on a scoped service, never a static field (rule R1).
/// </summary>
internal sealed class CallerSnapshot : ICallerSnapshot
{
    private readonly IIdentityQueries _identityQueries;

    private Guid? _identityFor;
    private UserSecurityIdentity? _identity;

    private (Guid UserId, Guid? TenantId)? _permissionsFor;
    private IReadOnlyCollection<string> _permissions = [];

    public CallerSnapshot(IIdentityQueries identityQueries) => _identityQueries = identityQueries;

    public async Task<UserSecurityIdentity?> GetIdentityAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_identityFor != userId)
        {
            _identity = await _identityQueries.GetSecurityIdentityAsync(userId, cancellationToken);
            _identityFor = userId;
        }

        return _identity;
    }

    public async Task<UserSecuritySnapshot?> GetAsync(Guid userId, Guid? tenantId, CancellationToken cancellationToken)
    {
        UserSecurityIdentity? identity = await GetIdentityAsync(userId, cancellationToken);
        if (identity is null)
        {
            return null;
        }

        // Keyed by (user, tenant), not user: a set computed for gym A must never answer for gym B,
        // even if the pipeline order that makes a second tenant impossible today is changed again.
        if (_permissionsFor != (userId, tenantId))
        {
            _permissions = await _identityQueries.GetEffectivePermissionsAsync(userId, tenantId, cancellationToken);
            _permissionsFor = (userId, tenantId);
        }

        return new UserSecuritySnapshot(userId, tenantId, identity.SecurityStamp, identity.Status, _permissions);
    }
}
