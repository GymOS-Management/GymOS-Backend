namespace GymOS.BuildingBlocks.Application.Abstractions;

/// <summary>
/// Answers whether the current caller holds a permission (AD-5/AD-22). One seam serves
/// both enforcement layers — the endpoint policy and the handler pipeline — so the two
/// can never disagree about what a permission means.
///
/// Implementations resolve from storage rather than from token claims. That costs a query
/// per request and buys immediate effect: revoking a role takes hold on the caller's very
/// next request instead of whenever their access token happens to expire.
/// </summary>
public interface IPermissionEvaluator
{
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken);

    /// <summary>
    /// Everything the caller holds — for <c>/auth/me</c> and for issuing tokens. Callers
    /// deciding a single permission should ask <see cref="HasPermissionAsync"/> instead of
    /// filtering this themselves, so the decision stays in one place.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetPermissionsAsync(CancellationToken cancellationToken);
}
