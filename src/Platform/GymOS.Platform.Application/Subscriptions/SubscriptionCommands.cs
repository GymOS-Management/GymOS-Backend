using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Invoices;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>
/// Puts a gym on a plan — assigning one if it has no live subscription, or moving it if it does. One
/// endpoint for both, because "this gym is on PRO, billed yearly in euros" is one intent and the caller
/// should not have to know which case it is (§3.3 names one route for assign/change).
/// </summary>
/// <param name="BillingCycle">Which of the plan's offered cycles. Must exist as a price.</param>
/// <param name="Currency">Which of the plan's offered currencies for that cycle.</param>
[RequiresPermission(PlatformPermissions.SubscriptionsManage)]
public sealed record AssignSubscriptionCommand(
    Guid GymId,
    Guid PlanId,
    string BillingCycle,
    string Currency,
    string? ExpectedRowVersion) : ICommand<Result<SubscriptionDetail>>;

/// <summary>
/// Cancels a gym's subscription.
/// </summary>
/// <param name="Immediately">
/// <see langword="false"/> (the default a caller should want) schedules cancellation for the end of the
/// paid period, so the gym keeps what it paid for. <see langword="true"/> ends it now and forfeits the
/// remainder — a deliberate choice, not a convenience.
/// </param>
[RequiresPermission(PlatformPermissions.SubscriptionsManage)]
public sealed record CancelSubscriptionCommand(
    Guid GymId,
    string? Reason,
    bool Immediately,
    string? ExpectedRowVersion) : ICommand<Result<SubscriptionDetail>>;

/// <summary>Undoes a scheduled cancellation, before the period it was scheduled for ends.</summary>
[RequiresPermission(PlatformPermissions.SubscriptionsManage)]
public sealed record ResumeSubscriptionCommand(Guid GymId, string? ExpectedRowVersion)
    : ICommand<Result<SubscriptionDetail>>;

/// <summary>
/// Converts a trial to a paying subscription.
///
/// <para>Explicit for now. The renewal job (M3.4) will do this on the trial's end date; until it exists,
/// a trial that has run out stays <c>Trialing</c> and the API reports <c>trialEndsAtUtc</c> in the past
/// so the state is legible rather than wrong.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.SubscriptionsManage)]
public sealed record ConvertTrialCommand(Guid GymId, string? ExpectedRowVersion)
    : ICommand<Result<SubscriptionDetail>>;

/// <summary>
/// Records that payment is late, or that it has arrived.
///
/// <para>Service is not withheld either way: whether a late-paying gym keeps operating is a separate
/// decision taken by suspending the gym. Every failed card must not end a business's day.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.SubscriptionsManage)]
public sealed record SetSubscriptionPaymentStateCommand(
    Guid GymId,
    bool PastDue,
    string? ExpectedRowVersion) : ICommand<Result<SubscriptionDetail>>;

internal sealed class AssignSubscriptionCommandHandler
    : ICommandHandler<AssignSubscriptionCommand, Result<SubscriptionDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IPlanRepository _plans;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ISubscriptionQueries _subscriptionQueries;
    private readonly InvoiceIssuer _issuer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public AssignSubscriptionCommandHandler(
        IGymRepository gyms,
        IPlanRepository plans,
        ISubscriptionRepository subscriptions,
        ISubscriptionQueries subscriptionQueries,
        InvoiceIssuer issuer,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _gyms = gyms;
        _plans = plans;
        _subscriptions = subscriptions;
        _subscriptionQueries = subscriptionQueries;
        _issuer = issuer;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result<SubscriptionDetail>> HandleAsync(
        AssignSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure<SubscriptionDetail>(GymErrors.NotFound(command.GymId));
        }

        // A suspended gym may be assigned a plan — resolving a billing dispute often *is* fixing the
        // subscription. A deleted one may not: there is nothing left to bill.
        if (gym.IsDeleted)
        {
            return Result.Failure<SubscriptionDetail>(SubscriptionErrors.GymDeleted);
        }

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<SubscriptionDetail>(PlanErrors.NotFound(command.PlanId));
        }

        Result<SubscriptionTerms> terms = ResolveTerms(plan, command.BillingCycle, command.Currency);
        if (terms.IsFailure)
        {
            return Result.Failure<SubscriptionDetail>(terms.Error!);
        }

        GymSubscription? existing = await _subscriptions.FindLiveByGymAsync(
            command.GymId, cancellationToken);

        try
        {
            if (existing is null)
            {
                // A withdrawn plan cannot be newly assigned (§2.3) — but a gym already on one keeps it,
                // which is why this check sits here and not in ChangePlan's path unconditionally.
                if (!plan.IsActive)
                {
                    return Result.Failure<SubscriptionDetail>(SubscriptionErrors.PlanInactive(plan.Code));
                }

                GymSubscription assigned = GymSubscription.Assign(
                    command.GymId, plan, terms.Value, _clock.UtcNow);

                _subscriptions.Add(assigned);

                if (assigned.Status == SubscriptionStatus.Active)
                {
                    // Trials bill at conversion, not at assignment — nothing is owed until the gym
                    // commits. The issuer only Adds; the SaveChangesAsync below commits subscription
                    // and invoice together or not at all.
                    await _issuer.IssueForPeriodAsync(assigned, plan, cancellationToken);
                }
            }
            else
            {
                Result version = SubscriptionWrites.ApplyExpectedVersion(
                    _subscriptions, existing, command.ExpectedRowVersion, versionRequired: false);
                if (version.IsFailure)
                {
                    return Result.Failure<SubscriptionDetail>(version.Error!);
                }

                if (!plan.IsActive && existing.PlanId != plan.Id)
                {
                    return Result.Failure<SubscriptionDetail>(SubscriptionErrors.PlanInactive(plan.Code));
                }

                // Terms are compared by value rather than by price-row id, so a negotiated
                // subscription (null price id) answers the same way.
                bool sameTerms = existing.PlanId == plan.Id
                    && existing.BillingCycle == terms.Value.BillingCycle
                    && existing.Price == terms.Value.Price;

                if (sameTerms)
                {
                    // Idempotent on purpose — this is a PUT, and a retry after a lost response must
                    // succeed. "Put me on the terms I already have" leaves the subscription with no
                    // pending change: cancelling one if scheduled, changing nothing otherwise.
                    existing.CancelPendingPlanChange();
                }
                else if (existing.Status == SubscriptionStatus.Trialing)
                {
                    // Nothing has been paid, so there is nothing to prorate and nothing to credit.
                    // The trial continues on the new plan, and conversion bills that plan's full
                    // first period — trials bill at conversion, never before.
                    existing.ChangePlan(plan, terms.Value);
                }
                else if (_clock.UtcNow >= existing.CurrentPeriodEndUtc)
                {
                    // The paid period is over, so there is no remainder to price: apply now, bill
                    // nothing. Scheduling here would date the change in the past and park it until
                    // a job that does not exist yet — an admin resolving a lapsed account needs the
                    // change to be real when the API says success.
                    existing.ChangePlan(plan, terms.Value);
                }
                else if (existing.Status == SubscriptionStatus.PastDue)
                {
                    // The current period is not settled: crediting "unused time" the gym never paid
                    // for would be wrong, and stacking a proration charge on top of an unpaid
                    // invoice would be worse. The change waits at the period boundary, where no
                    // proration arises.
                    existing.SchedulePlanChange(plan, terms.Value, existing.CurrentPeriodEndUtc);
                }
                else if (existing.BillingCycle != terms.Value.BillingCycle)
                {
                    // Cross-cycle proration is ill-defined: the two prices buy different lengths of
                    // time, and allocating an annual price over the days of a monthly period would
                    // charge a year's money for weeks of service. At the period boundary no
                    // proration arises, so that is where a cycle changes.
                    existing.SchedulePlanChange(plan, terms.Value, existing.CurrentPeriodEndUtc);
                }
                else
                {
                    // The net-sign policy (M3.3 design §net-sign): money owed to the platform is
                    // billed now; money owed to the gym is never refunded mid-period — the change
                    // waits for the period end instead, because there is no credit-note document to
                    // carry a negative invoice.
                    ProrationResult? proration = Proration.Compute(
                        existing.Price,
                        terms.Value.Price,
                        existing.CurrentPeriodStartUtc,
                        existing.CurrentPeriodEndUtc,
                        _clock.UtcNow);

                    if (proration is null || proration.Net.IsNegative)
                    {
                        // Null is nearly unreachable here — the expired-period branch above catches
                        // an ended period — but a sub-day remainder rounds to zero days, and
                        // scheduling at the imminent boundary is the honest answer for it too.
                        existing.SchedulePlanChange(plan, terms.Value, existing.CurrentPeriodEndUtc);
                    }
                    else
                    {
                        existing.ChangePlan(plan, terms.Value);

                        if (proration.Net.Amount > 0m)
                        {
                            await _issuer.IssueProrationAsync(
                                existing, plan, proration, cancellationToken);
                        }

                        // A net of exactly zero changes the plan and bills nothing — the two plans
                        // cost the same from here to the period end.
                    }
                }
            }
        }
        catch (DomainException exception)
        {
            return Result.Failure<SubscriptionDetail>(
                Error.Conflict("subscriptions.invalid_state", exception.Message));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await SubscriptionWrites.ReadBackAsync(
            _subscriptionQueries, command.GymId, cancellationToken);
    }

    /// <summary>
    /// Finds the plan's price for the requested cycle and currency.
    ///
    /// Refused rather than defaulted when absent: picking "the nearest price" would bill a gym an amount
    /// nobody agreed to.
    /// </summary>
    private static Result<SubscriptionTerms> ResolveTerms(
        SubscriptionPlan plan,
        string? billingCycle,
        string? currency)
    {
        Result<BillingCycle> cycle = PlanWrites.ParseBillingCycle(billingCycle);
        if (cycle.IsFailure)
        {
            return Result.Failure<SubscriptionTerms>(cycle.Error!);
        }

        Result<Currency> parsedCurrency = PlanWrites.ParseCurrency(currency);
        if (parsedCurrency.IsFailure)
        {
            return Result.Failure<SubscriptionTerms>(parsedCurrency.Error!);
        }

        PlanPrice? price = plan.Prices
            .SingleOrDefault(candidate => candidate.Matches(cycle.Value, parsedCurrency.Value));

        return price is null
            ? Result.Failure<SubscriptionTerms>(
                SubscriptionErrors.PriceNotOffered(cycle.Value.ToString(), parsedCurrency.Value.Code))
            : Result.Success(SubscriptionTerms.From(price));
    }
}

/// <summary>
/// The four state-changing operations, together, because they share their shape: load the live
/// subscription, honour the precondition, ask the aggregate, read back.
/// </summary>
internal sealed class SubscriptionStateCommandHandlers :
    ICommandHandler<CancelSubscriptionCommand, Result<SubscriptionDetail>>,
    ICommandHandler<ResumeSubscriptionCommand, Result<SubscriptionDetail>>,
    ICommandHandler<ConvertTrialCommand, Result<SubscriptionDetail>>,
    ICommandHandler<SetSubscriptionPaymentStateCommand, Result<SubscriptionDetail>>
{
    private readonly ISubscriptionRepository _subscriptions;
    private readonly ISubscriptionQueries _subscriptionQueries;
    private readonly IPlanRepository _plans;
    private readonly InvoiceIssuer _issuer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public SubscriptionStateCommandHandlers(
        ISubscriptionRepository subscriptions,
        ISubscriptionQueries subscriptionQueries,
        IPlanRepository plans,
        InvoiceIssuer issuer,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _subscriptions = subscriptions;
        _subscriptionQueries = subscriptionQueries;
        _plans = plans;
        _issuer = issuer;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result<SubscriptionDetail>> HandleAsync(
        CancelSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return MutateAsync(
            command.GymId,
            command.ExpectedRowVersion,
            subscription =>
            {
                if (command.Immediately)
                {
                    subscription.CancelNow(command.Reason, _clock.UtcNow);
                    return Result.Success();
                }

                if (subscription.CancelAtPeriodEnd)
                {
                    return Result.Failure(SubscriptionErrors.AlreadyScheduledForCancellation);
                }

                subscription.ScheduleCancellation(command.Reason);
                return Result.Success();
            },
            cancellationToken);
    }

    public Task<Result<SubscriptionDetail>> HandleAsync(
        ResumeSubscriptionCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return MutateAsync(
            command.GymId,
            command.ExpectedRowVersion,
            subscription =>
            {
                if (!subscription.CancelAtPeriodEnd)
                {
                    return Result.Failure(SubscriptionErrors.NoCancellationScheduled);
                }

                subscription.WithdrawCancellation();
                return Result.Success();
            },
            cancellationToken);
    }

    public Task<Result<SubscriptionDetail>> HandleAsync(
        ConvertTrialCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return MutateAsync(
            command.GymId,
            command.ExpectedRowVersion,
            async subscription =>
            {
                if (subscription.Status != SubscriptionStatus.Trialing)
                {
                    return Result.Failure(SubscriptionErrors.NotTrialing);
                }

                subscription.ConvertTrial();

                // A trial longer than one billing cycle left lapsed periods behind it; the first
                // real invoice must cover the period the gym is IN, not one that expired mid-trial.
                subscription.CatchUpPeriods(_clock.UtcNow);

                // Conversion is the moment the gym starts paying, so it bills the first period —
                // the invoice an untrialed assignment would have issued up front. The plan is
                // loaded for its name on the line; a subscription always points at one.
                SubscriptionPlan? plan = await _plans.FindByIdAsync(
                    subscription.PlanId, cancellationToken);
                if (plan is null)
                {
                    return Result.Failure(PlanErrors.NotFound(subscription.PlanId));
                }

                await _issuer.IssueForPeriodAsync(subscription, plan, cancellationToken);
                return Result.Success();
            },
            cancellationToken);
    }

    public Task<Result<SubscriptionDetail>> HandleAsync(
        SetSubscriptionPaymentStateCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return MutateAsync(
            command.GymId,
            command.ExpectedRowVersion,
            subscription =>
            {
                if (command.PastDue)
                {
                    if (subscription.Status == SubscriptionStatus.PastDue)
                    {
                        return Result.Failure(SubscriptionErrors.AlreadyPastDue);
                    }

                    subscription.MarkPastDue();
                    return Result.Success();
                }

                if (subscription.Status != SubscriptionStatus.PastDue)
                {
                    return Result.Failure(SubscriptionErrors.NotPastDue);
                }

                subscription.ResumePayment();
                return Result.Success();
            },
            cancellationToken);
    }

    private Task<Result<SubscriptionDetail>> MutateAsync(
        Guid gymId,
        string? expectedRowVersion,
        Func<GymSubscription, Result> mutate,
        CancellationToken cancellationToken) =>
        MutateAsync(
            gymId,
            expectedRowVersion,
            subscription => Task.FromResult(mutate(subscription)),
            cancellationToken);

    private async Task<Result<SubscriptionDetail>> MutateAsync(
        Guid gymId,
        string? expectedRowVersion,
        Func<GymSubscription, Task<Result>> mutate,
        CancellationToken cancellationToken)
    {
        GymSubscription? subscription = await _subscriptions.FindLiveByGymAsync(gymId, cancellationToken);
        if (subscription is null)
        {
            return Result.Failure<SubscriptionDetail>(SubscriptionErrors.NotFound(gymId));
        }

        Result version = SubscriptionWrites.ApplyExpectedVersion(
            _subscriptions, subscription, expectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<SubscriptionDetail>(version.Error!);
        }

        Result mutation;
        try
        {
            mutation = await mutate(subscription);
        }
        catch (DomainException exception)
        {
            return Result.Failure<SubscriptionDetail>(
                Error.Conflict("subscriptions.invalid_state", exception.Message));
        }

        if (mutation.IsFailure)
        {
            return Result.Failure<SubscriptionDetail>(mutation.Error!);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // A cancelled subscription is no longer *live*, so the read-back has to look it up by id rather
        // than by "the gym's live one" — which would now correctly return nothing.
        SubscriptionDetail? detail = await _subscriptionQueries.GetLiveForGymAsync(gymId, cancellationToken)
            ?? (await _subscriptionQueries.ListHistoryForGymAsync(gymId, cancellationToken))
                .FirstOrDefault(candidate => candidate.Id == subscription.Id);

        return detail is null
            ? Result.Failure<SubscriptionDetail>(SubscriptionErrors.NotFound(gymId))
            : Result.Success(detail);
    }
}
