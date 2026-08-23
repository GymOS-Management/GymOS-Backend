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

    /// <summary>
    /// The unique index behind <c>ux_gym_feature_overrides_gym_feature</c> (one row per gym,
    /// feature — see <c>GymFeatureOverrideConfiguration</c>). Named here so the retry below only
    /// catches the one race it knows how to resolve.
    /// </summary>
    private const string GymFeatureConstraint = "ux_gym_feature_overrides_gym_feature";

    public async Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> HandleAsync(
        SetGymFeaturesCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The same refusal its sibling route makes, and for the same stated reason: treating "the
        // caller forgot the field" as "the caller deliberately sent nothing" hides a mistake instead of
        // catching it. This route used to coerce the null to an empty list, open a transaction, change
        // nothing, and return 200 with the full resolved catalogue — indistinguishable from a
        // successful write, and accepted despite the committed OpenAPI contract marking the field
        // required on SetGymFeaturesRequest.
        if (command.Features is null)
        {
            return Result.Failure<IReadOnlyList<ResolvedFeatureDetail>>(FeatureErrors.FeaturesRequired);
        }

        try
        {
            return await ExecuteAsync(command, cancellationToken);
        }
        // Both races are caught, because the insert/insert one is not the only one — and not even the
        // reachable one in the common case.
        //
        // A gym WITH a live plan is serialised by the UPDLOCK read below before its overrides are read,
        // so a second concurrent request waits and then restates cleanly: no race. The unserialised
        // window is a gym with NO live subscription, where no lock is taken at all. On exactly that
        // path, if the gym already holds the override, two concurrent requests both load it with the
        // same row version and both restate; the loser's UPDATE matches nothing and surfaces as
        // ConcurrencyConflictException, never as a uniqueness violation. Clearing an override fails
        // the same way.
        //
        // So catching only UniquenessConflictException left the documented promise — "no If-Match,
        // unlike the plan and catalogue writes... the later decision wins" — false for restate and
        // clear, which are the operations an operator repeats most. The retry could only ever help a
        // first-time grant.
        catch (Exception exception) when (IsConcurrentDecisionOnTheSameException(exception))
        {
            // Two callers granting the same (gym, feature) exception concurrently both read "no
            // existing override" and both insert; the index lets exactly one through, and the loser
            // used to get a 409 telling it to "choose a value that is still free" — advice nobody
            // can follow, since there is no other value, for a request that would succeed if simply
            // retried. Retrying here is what makes the documented promise true: "the later decision
            // wins, and the audit trail keeps the earlier one".
            //
            // DiscardTrackedChanges first, or the retry inherits this attempt's poisoned state: the
            // insert that lost is still tracked as Added (the database rolled it back, but this
            // request-scoped DbContext does not know that), and re-running PlanChangesAsync without
            // clearing it would try to write that same doomed row again alongside whatever the
            // retry decides, failing the same way a second time. Clearing first means the retry's
            // fresh reads see only what is actually committed — the winner's row — so it correctly
            // finds an existing override and Restates it instead of trying to insert another one.
            //
            // A second failure past this point is a different, real conflict — not this race — and
            // is left to surface as the 409 it is.
            _unitOfWork.DiscardTrackedChanges();
            return await ExecuteAsync(command, cancellationToken);
        }
    }

    /// <summary>
    /// Whether this failure is another caller deciding the same gym's same exception at the same
    /// moment — the one race this endpoint retries, because it deliberately has no <c>If-Match</c> and
    /// promises the later decision wins.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. A uniqueness violation on any other constraint, or a concurrency conflict on
    /// any other aggregate, is a genuine conflict this handler has no business retrying, and is left to
    /// surface as the 409 it is.
    /// </remarks>
    private static bool IsConcurrentDecisionOnTheSameException(Exception exception) =>
        exception switch
        {
            UniquenessConflictException uniqueness => uniqueness.ConstraintName == GymFeatureConstraint,
            ConcurrencyConflictException => true,
            _ => false,
        };

    private async Task<Result<IReadOnlyList<ResolvedFeatureDetail>>> ExecuteAsync(
        SetGymFeaturesCommand command,
        CancellationToken cancellationToken)
    {
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

        // The whole decision-and-write is one explicit transaction over an UPDLOCK read of the
        // gym's live plan (inside PlanChangesAsync → LoadPlanFeaturesAsync): without the lock held
        // across both, a concurrent PUT /plans/{id}/features could change the plan's opinion between
        // the moment this request decides "needs an override" and the moment it saves, so the 200 it
        // returns would assert a resolution the gym does not actually resolve to.
        await using IUnitOfWorkTransaction transaction =
            await _unitOfWork.BeginTransactionAsync(cancellationToken);

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

        // Read back before committing, not after. The UPDLOCK above releases the instant
        // this transaction ends, and a plan write blocked on it is free to run the moment that
        // happens — if the resolve query ran after CommitAsync, that unblocked plan write could
        // land in the gap between the commit and the resolve, and the body returned to the caller
        // would already be stale by the time it left the database. IFeatureQueries and this
        // handler's repositories share one scoped AppDbContext (registered once per request), so
        // this query runs on the same connection, inside the same transaction, and sees exactly
        // the row values this transaction is about to make durable — nothing else can be
        // interleaved before it.
        IReadOnlyList<ResolvedFeatureDetail> resolution =
            await _featureQueries.ResolveForGymAsync(command.GymId, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // The resolution, not an echo — see the command's remarks.
        return Result.Success(resolution);
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
    ///
    /// <para>Not shared with <see cref="FeatureExceptionMaintainer"/>: that class was given exactly
    /// this method once (M4.1 task 2), and the sharing was the defect — the read here is correct
    /// because it answers "what does the database say right now", which is exactly the question a
    /// fresh <c>PUT</c> request should ask, and exactly the question the maintainer must
    /// <em>not</em> ask when it is reacting to a change its own caller has not saved yet. See
    /// <see cref="IFeatureExceptionMaintainer.WithdrawRedundantAsync"/>.</para>
    ///
    /// <para>The plan itself is read <c>FOR SHARE</c> (<see cref="IPlanRepository.
    /// FindByIdForShareAsync"/>), held by the caller's transaction through the save: without the
    /// lock, a concurrent <c>PUT /plans/{id}/features</c> could change what this plan says about a
    /// feature after this method reads it but before this request's write commits, so the decision
    /// made here would no longer match the plan by the time it took effect.</para>
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

        SubscriptionPlan? plan = await _plans.FindByIdForShareAsync(subscription.PlanId, cancellationToken);

        return plan is null ? [] : [.. plan.Features];
    }
}
