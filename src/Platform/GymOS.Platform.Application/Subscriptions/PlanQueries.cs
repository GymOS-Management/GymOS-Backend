using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Models;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record ListPlansQuery(PageRequest Page, bool IncludeInactive, bool? IsPublic)
    : IQuery<Result<PagedResult<PlanListItem>>>;

internal sealed class ListPlansQueryHandler
    : IQueryHandler<ListPlansQuery, Result<PagedResult<PlanListItem>>>
{
    private readonly IPlanQueries _planQueries;

    public ListPlansQueryHandler(IPlanQueries planQueries) => _planQueries = planQueries;

    public async Task<Result<PagedResult<PlanListItem>>> HandleAsync(
        ListPlansQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        PagedResult<PlanListItem> page = await _planQueries.ListAsync(
            new PlanListFilter(query.Page, query.IncludeInactive, query.IsPublic),
            cancellationToken);

        return Result.Success(page);
    }
}

/// <summary>
/// One plan with its prices. An inactive plan still resolves — an operator asking why a gym is on a
/// plan they cannot find in the catalogue needs to see it, and when it was withdrawn.
/// </summary>
[RequiresPermission(PlatformPermissions.BillingRead)]
public sealed record GetPlanQuery(Guid PlanId) : IQuery<Result<PlanDetail>>;

internal sealed class GetPlanQueryHandler : IQueryHandler<GetPlanQuery, Result<PlanDetail>>
{
    private readonly IPlanQueries _planQueries;

    public GetPlanQueryHandler(IPlanQueries planQueries) => _planQueries = planQueries;

    public async Task<Result<PlanDetail>> HandleAsync(
        GetPlanQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        PlanDetail? plan = await _planQueries.GetAsync(query.PlanId, cancellationToken);

        return plan is null
            ? Result.Failure<PlanDetail>(PlanErrors.NotFound(query.PlanId))
            : Result.Success(plan);
    }
}
