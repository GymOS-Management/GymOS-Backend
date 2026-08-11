using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Application.Gyms;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Features.Rules;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Features;

/// <summary>
/// One desired answer for one gym. <paramref name="Reason"/> is required only when the value actually
/// needs an override — asking for what the gym already gets clears one instead, and needs no
/// justification. See <see cref="SetGymFeaturesCommand"/>.
/// </summary>
public sealed record GymFeatureRequest(string Key, bool IsEnabled, string? Reason);

/// <summary>What one gym's features resolve to, and why (§3.4's <c>GET /gyms/{id}/features</c>).</summary>
[RequiresPermission(PlatformPermissions.FeaturesRead)]
public sealed record GetGymFeaturesQuery(Guid GymId)
    : IQuery<Result<IReadOnlyList<ResolvedFeatureDetail>>>;

/// <summary>
/// Sets or clears a gym's feature exceptions (§3.4's <c>PUT /gyms/{id}/features</c>).
///
/// <para><b>An override is stored only when it differs from what the gym would get anyway</b> (§2.4).
/// Asking for exactly what the plan — or the platform default, when the plan is silent — already
/// gives <em>clears</em> any existing exception rather than storing a redundant one, so a gym that
/// later changes plan follows the new plan instead of staying frozen by a row nobody remembers
/// creating.</para>
///
/// <para>Consequently <b>the response is the full resolution, not an echo</b> of the request: after a
/// clear the stored state is deliberately not what was sent, and echoing the request back would hide
/// that. It is also the answer the caller actually wants — "is POS on for this gym, and why".</para>
///
/// <para>Only the keys in the request are touched, so a client that knows about ten features cannot
/// silently withdraw an exception on an eleventh added by a later release.</para>
///
/// <para>Gated on <c>features.manage</c>, not billing: granting one gym an exception is day-to-day
/// administration a support agent should be able to do. Deciding what a <em>plan</em> includes is
/// pricing the product, and lives behind <c>billing.manage</c>.</para>
///
/// <para>No <c>If-Match</c>, unlike the plan and catalogue writes. There is no single version to
/// match: each override is its own aggregate keyed on (gym, feature), the request merges per key, and
/// two operators editing different features do not conflict at all. Two editing the same one is a
/// disagreement about policy rather than a lost update — the later decision wins, carries its own
/// required reason, and the audit trail keeps the earlier one.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.FeaturesManage)]
public sealed record SetGymFeaturesCommand(Guid GymId, IReadOnlyList<GymFeatureRequest> Features)
    : ICommand<Result<IReadOnlyList<ResolvedFeatureDetail>>>;

internal sealed class GetGymFeaturesQueryHandler
    : IQueryHandler<GetGymFeaturesQuery, Result<IReadOnlyList<ResolvedFeatureDetail>>>
{
    private readonly IGymQueries _gyms;
    private readonly IFeatureQueries _featureQueries;

    public GetGymFeaturesQueryHandler(IGymQueries gyms, IFeatureQueries featureQueries)
    {
        _gyms = gyms;
        _featureQueries = featureQueries;
    }

    public async Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> HandleAsync(
        GetGymFeaturesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // 404 for an unknown gym rather than a catalogue of defaults: "this gym's features" and
        // "the platform's features" are different questions, and answering the second when asked
        // the first hides a wrong id behind a plausible-looking body. A deleted gym still answers —
        // an operator investigating a dead tenant needs to see what it had.
        if (!await _gyms.ExistsAsync(query.GymId, cancellationToken))
        {
            return Result.Failure<IReadOnlyList<ResolvedFeatureDetail>>(GymErrors.NotFound(query.GymId));
        }

        return Result.Success(await _featureQueries.ResolveForGymAsync(query.GymId, cancellationToken));
    }
}

internal sealed class SetGymFeaturesCommandHandler
    : ICommandHandler<SetGymFeaturesCommand, Result<IReadOnlyList<ResolvedFeatureDetail>>>
{
    private readonly IGymRepository _gyms;
    private readonly IFeatureRepository _features;
    private readonly IPlanRepository _plans;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IFeatureQueries _featureQueries;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public SetGymFeaturesCommandHandler(
        IGymRepository gyms,
        IFeatureRepository features,
        IPlanRepository plans,
        ISubscriptionRepository subscriptions,
        IFeatureQueries featureQueries,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IClock clock)
    {
        _gyms = gyms;
        _features = features;
        _plans = plans;
        _subscriptions = subscriptions;
        _featureQueries = featureQueries;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _clock = clock;
    }

    /// <summary>One request paired with everything needed to act on it, once validation has passed.</summary>
    private sealed record Planned(
        Feature Feature,
        bool IsEnabled,
        string Reason,
        bool NeedsOverride,
        GymFeatureOverride? Existing);

    public async Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> HandleAsync(
        SetGymFeaturesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure<IReadOnlyList<ResolvedFeatureDetail>>(
                GymErrors.NotFound(command.GymId));
        }

        // A deleted gym is not configurable: it has no access to grant a capability to, and M3.2
        // already stopped billing it. Suspended is deliberately allowed — an operator often needs to
        // adjust what a gym gets *while* it is suspended, before letting it back in.
        if (gym.IsDeleted)
        {
            return Result.Failure<IReadOnlyList<ResolvedFeatureDetail>>(GymErrors.Deleted);
        }

        // Everything is validated before anything is written: one bad key or one missing reason
        // rejects the whole request. A half-applied feature set is worse than a refused one, because
        // the caller cannot tell which half landed.
        Result<List<Planned>> plan = await PlanChangesAsync(command, cancellationToken);
        if (plan.IsFailure)
        {
            return Result.Failure<IReadOnlyList<ResolvedFeatureDetail>>(plan.Error!);
        }

        DateTimeOffset now = _clock.UtcNow;

        foreach (Planned change in plan.Value)
        {
            if (!change.NeedsOverride)
            {
                // The gym already gets this without an exception, so withdraw one if it exists.
                // Nothing to do otherwise — and that is a success: the caller's intent is already
                // true, and the point of the request was the outcome, not the row.
                if (change.Existing is not null)
                {
                    change.Existing.MarkCleared(change.Feature.Key);
                    _features.RemoveOverride(change.Existing);
                }

                continue;
            }

            if (change.Existing is null)
            {
                _features.AddOverride(GymFeatureOverride.Grant(
                    command.GymId,
                    change.Feature.Id,
                    change.Feature.Key,
                    change.IsEnabled,
                    change.Reason,
                    _currentUser.UserId,
                    now));
            }
            else
            {
                change.Existing.Restate(
                    change.Feature.Key,
                    change.IsEnabled,
                    change.Reason,
                    _currentUser.UserId,
                    now);
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // The resolution, not an echo — see the command's remarks.
        return Result.Success(
            await _featureQueries.ResolveForGymAsync(command.GymId, cancellationToken));
    }

    /// <summary>
    /// Turns the request into decided changes, or the first reason it cannot be honoured. Nothing is
    /// mutated here, which is what lets a rejection leave the gym untouched.
    /// </summary>
    private async Task<Result<List<Planned>>> PlanChangesAsync(
        SetGymFeaturesCommand command,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<Feature> catalogue = await _features.ListAllAsync(cancellationToken);
        Dictionary<string, Feature> byKey = catalogue.ToDictionary(
            feature => feature.Key, StringComparer.OrdinalIgnoreCase);

        // What the layers beneath the override currently say, so each request can be judged against
        // the answer the gym would get without one.
        IReadOnlyList<PlanFeature> planFeatures = await LoadPlanFeaturesAsync(
            command.GymId, cancellationToken);
        IReadOnlyList<GymFeatureOverride> existing = await _features.ListOverridesForGymAsync(
            command.GymId, cancellationToken);

        var planned = new List<Planned>(command.Features.Count);
        var seen = new HashSet<Guid>();
        foreach (GymFeatureRequest request in command.Features)
        {
            string key = (request.Key ?? string.Empty).Trim();
            if (!byKey.TryGetValue(key, out Feature? feature))
            {
                return Result.Failure<List<Planned>>(FeatureErrors.UnknownKey(key));
            }

            // The same key twice is a contradiction, not an instruction: there is one row per (gym,
            // feature), so silently applying the last one would honour half of what was asked while
            // reporting success. Refused instead — and refused here rather than at the unique index,
            // which would surface as an unexplained 409.
            if (!seen.Add(feature.Id))
            {
                return Result.Failure<List<Planned>>(FeatureErrors.DuplicateKey(feature.Key));
            }

            bool? planEnabled = planFeatures
                .SingleOrDefault(planFeature => planFeature.FeatureId == feature.Id)
                ?.IsEnabled;

            bool needsOverride = FeatureResolution.RequiresOverride(
                request.IsEnabled, planEnabled, feature.DefaultEnabled);

            string reason = (request.Reason ?? string.Empty).Trim();
            if (needsOverride && new OverrideReasonIsRequired(reason).IsBroken())
            {
                // Checked here rather than caught out of the aggregate: the domain rule stays the
                // guard of last resort, and refusing up front means a five-key request cannot write
                // three of them before discovering the fourth has no justification.
                return Result.Failure<List<Planned>>(FeatureErrors.ReasonRequired(feature.Key));
            }

            planned.Add(new Planned(
                feature,
                request.IsEnabled,
                reason,
                needsOverride,
                existing.SingleOrDefault(over => over.FeatureId == feature.Id)));
        }

        return Result.Success(planned);
    }

    /// <summary>
    /// The feature opinions of the gym's live plan, or none when it has no live subscription — in
    /// which case every feature resolves against the platform default, which is what
    /// <see cref="IFeatureQueries.ResolveForGymAsync"/> does too. The two must agree, or a write
    /// would decide "no override needed" against one rule and be read back under another.
    /// </summary>
    private async Task<IReadOnlyList<PlanFeature>> LoadPlanFeaturesAsync(
        Guid gymId,
        CancellationToken cancellationToken)
    {
        GymSubscription? subscription = await _subscriptions.FindLiveByGymAsync(gymId, cancellationToken);
        if (subscription is null)
        {
            return [];
        }

        SubscriptionPlan? plan = await _plans.FindByIdAsync(subscription.PlanId, cancellationToken);

        return plan is null ? [] : [.. plan.Features];
    }
}
