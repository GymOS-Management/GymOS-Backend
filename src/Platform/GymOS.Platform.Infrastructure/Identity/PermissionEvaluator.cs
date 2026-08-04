using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;

namespace GymOS.Platform.Infrastructure.Identity;

/// <summary>
/// Resolves the caller's permissions from storage (AD-22 §6.2, first term of the
/// effective-access formula) and answers both enforcement layers from the same snapshot.
///
/// Reading from the database rather than from token claims is a deliberate trade: it costs
/// a query, and it means a revoked role stops working on the caller's *next request*
/// instead of whenever their access token happens to expire. For an administrative console
/// where revocation usually follows an incident, that is the correct side of the trade.
/// The cost is paid once per request, not once per check (see <see cref="CallerSnapshot"/>).
/// </summary>
internal sealed class PermissionEvaluator : IPermissionEvaluator
{
    private readonly ICurrentUser _currentUser;
    private readonly ICallerSnapshot _snapshot;

    public PermissionEvaluator(ICurrentUser currentUser, ICallerSnapshot snapshot)
    {
        _currentUser = currentUser;
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

        UserSecuritySnapshot? snapshot = await _snapshot.GetAsync(userId, cancellationToken);

        // A suspended account holds nothing, whatever its roles say. Status is re-read per
        // request, so a suspension takes effect immediately rather than at token expiry.
        return snapshot is null || !string.Equals(snapshot.Status, "Active", StringComparison.Ordinal)
            ? []
            : snapshot.Permissions;
    }
}

/// <summary>
/// Memoizes the caller's security snapshot for the lifetime of one request.
///
/// Without this, a single request would re-read the same rows several times: the token
/// validator checks the security stamp, the endpoint policy checks a permission, and the
/// pipeline behavior checks it again. Scoped, so the cache cannot outlive the request and
/// serve a stale answer to the next one — and instance state on a scoped service, never a
/// static field (rule R1).
/// </summary>
internal sealed class CallerSnapshot : ICallerSnapshot
{
    private readonly IIdentityQueries _identityQueries;
    private UserSecuritySnapshot? _cached;
    private Guid? _cachedFor;

    public CallerSnapshot(IIdentityQueries identityQueries) => _identityQueries = identityQueries;

    public async Task<UserSecuritySnapshot?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_cachedFor == userId)
        {
            return _cached;
        }

        _cached = await _identityQueries.GetSecuritySnapshotAsync(userId, cancellationToken);
        _cachedFor = userId;

        return _cached;
    }
}
