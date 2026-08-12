using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Gyms;

/// <param name="IncludeDeleted">
/// Off by default; see <see cref="GymListFilter"/> for why a deleted tenant should not appear in a figure
/// someone reports upward.
/// </param>
[RequiresPermission(PlatformPermissions.GymsRead)]
public sealed record ListGymsQuery(
    PageRequest Page,
    string? Status,
    string? CountryCode,
    bool IncludeDeleted) : IQuery<Result<PagedResult<GymListItem>>>;

internal sealed class ListGymsQueryHandler
    : IQueryHandler<ListGymsQuery, Result<PagedResult<GymListItem>>>
{
    private readonly IGymQueries _gymQueries;

    public ListGymsQueryHandler(IGymQueries gymQueries) => _gymQueries = gymQueries;

    public async Task<Result<PagedResult<GymListItem>>> HandleAsync(
        ListGymsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        PagedResult<GymListItem> page = await _gymQueries.ListAsync(
            new GymListFilter(query.Page, query.Status, query.CountryCode, query.IncludeDeleted),
            cancellationToken);

        return Result.Success(page);
    }
}

/// <summary>
/// One gym in full. Returns a soft-deleted gym rather than 404 — an operator investigating why a tenant
/// stopped working needs to see that it was deleted, and when.
/// </summary>
[RequiresPermission(PlatformPermissions.GymsRead)]
public sealed record GetGymQuery(Guid GymId) : IQuery<Result<GymDetail>>;

internal sealed class GetGymQueryHandler : IQueryHandler<GetGymQuery, Result<GymDetail>>
{
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;

    public GetGymQueryHandler(IGymQueries gymQueries, IPermissionEvaluator permissionEvaluator)
    {
        _gymQueries = gymQueries;
        _permissionEvaluator = permissionEvaluator;
    }

    public async Task<Result<GymDetail>> HandleAsync(GetGymQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The same evaluator AuthorizationBehavior and the endpoint policies use, so this can never
        // disagree with them about who holds features.read. gyms.read is already guaranteed by the
        // pipeline (this query's own RequiresPermission) before the handler runs, so only the second,
        // narrower permission needs asking here. GymWrites.ReadBackAsync asks the same question for
        // every write route that returns a GymDetail (M4.1 task 4) — the two are Application-layer
        // siblings, not two implementations of the same idea.
        bool includeFeatures = await _permissionEvaluator.HasPermissionAsync(
            PlatformPermissions.FeaturesRead, cancellationToken);

        GymDetail? gym = await _gymQueries.GetAsync(query.GymId, includeFeatures, cancellationToken);

        return gym is null
            ? Result.Failure<GymDetail>(GymErrors.NotFound(query.GymId))
            : Result.Success(gym);
    }
}
