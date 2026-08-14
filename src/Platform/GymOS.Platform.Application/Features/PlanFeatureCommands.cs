using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Subscriptions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// One desired plan opinion. <paramref name="IsEnabled"/> null means <b>clear</b> the opinion, so
/// gyms on the plan fall through to the platform default — which is a different answer from
/// <c>false</c>, and the reason this is a nullable rather than a bool (see <c>PlanFeature</c>).
/// </summary>
public sealed record PlanFeatureRequest(string Key, bool? IsEnabled);

/// <summary>
/// Sets what a plan includes (§3.4's <c>/plans/{id}/features</c>).
///
/// <para><b>Gated on billing, not on features</b>: what a plan includes is priced, and deciding it
/// changes what every gym on the plan gets. Granting one gym an exception is the other permission,
/// and the split is deliberate — support staff should be able to do the second without holding the
/// authority to redefine the product.</para>
///
/// <para>Only the keys in the request are touched. Features the caller does not mention keep
/// whatever the plan already said, so a client that knows about ten features cannot silently erase
/// an eleventh added by a later release.</para>
/// </summary>
/// <param name="ExpectedRowVersion">
/// <c>If-Match</c>, <b>required</b>: this overwrites what the caller read, and there is no state
/// machine to refuse a stale interleaving — the M3.1 price lesson, applied.
/// </param>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record SetPlanFeaturesCommand(
    Guid PlanId,
    IReadOnlyList<PlanFeatureRequest> Features,
    string? ExpectedRowVersion) : ICommand<Result<PlanDetail>>;

internal sealed class SetPlanFeaturesCommandHandler
    : ICommandHandler<SetPlanFeaturesCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IFeatureRepository _features;
    private readonly IUnitOfWork _unitOfWork;

    public SetPlanFeaturesCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IFeatureRepository features,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _features = features;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        SetPlanFeaturesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.NotFound(command.PlanId));
        }

        Result version = PlanWrites.ApplyExpectedVersion(
            _plans, plan, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<PlanDetail>(version.Error!);
        }

        // Guarded here rather than at the endpoint, and after the two checks above rather than before
        // them. In the delegate it preempted this handler's own answers: a PUT to a nonexistent plan
        // with no `features` field reported the field instead of the 404, and one missing both the
        // field and If-Match reported the field instead of the 428 — so a caller who fixed what they
        // were told failed again for a reason they had not been told. It also left the NullReference
        // below reachable for every non-HTTP caller, since a job or a test does not pass through the
        // delegate at all.
        if (command.Features is null)
        {
            return Result.Failure<PlanDetail>(FeatureErrors.FeaturesRequired);
        }

        // Resolve every key first, so a request naming one unknown feature changes nothing at all.
        // A partially applied feature set is worse than a rejected one: the caller cannot tell which
        // half landed.
        IReadOnlyList<Feature> catalogue = await _features.ListAllAsync(cancellationToken);
        Dictionary<string, Feature> byKey = catalogue.ToDictionary(
            feature => feature.Key, StringComparer.OrdinalIgnoreCase);

        var resolved = new List<(Feature Feature, bool? IsEnabled)>(command.Features.Count);
        var seen = new HashSet<Guid>();
        foreach (PlanFeatureRequest requested in command.Features)
        {
            if (!byKey.TryGetValue((requested.Key ?? string.Empty).Trim(), out Feature? feature))
            {
                return Result.Failure<PlanDetail>(
                    FeatureErrors.UnknownKey(requested.Key ?? string.Empty));
            }

            // The same key twice is a contradiction, not an instruction: there is one opinion per
            // (plan, feature), so silently applying the last one would honour half of what was
            // asked while reporting success. SetGymFeaturesCommandHandler.PlanChangesAsync refuses
            // its own repeated key for exactly this reason, and it applies verbatim here.
            if (!seen.Add(feature.Id))
            {
                return Result.Failure<PlanDetail>(FeatureErrors.DuplicateKey(feature.Key));
            }

            resolved.Add((feature, requested.IsEnabled));
        }

        var changed = false;
        foreach ((Feature feature, bool? isEnabled) in resolved)
        {
            changed |= isEnabled is { } enabled
                ? plan.SetFeature(feature.Id, enabled)
                : plan.ClearFeature(feature.Id);
        }

        // Feature rows live in a child table, so without this the plan's own row is never updated
        // and its version never moves — leaving concurrent feature edits unprotected. The same
        // reasoning as prices.
        //
        // Only when something actually changed, though: a PUT restating what the plan already says
        // is a legitimate retry, and bumping the version for it would invalidate every other
        // caller's If-Match over a write that did nothing.
        if (changed)
        {
            _plans.MarkAggregateChanged(plan);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}
