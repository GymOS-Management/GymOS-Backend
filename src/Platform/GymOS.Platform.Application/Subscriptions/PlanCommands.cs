using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Currencies;
using GymOS.SharedKernel.Results;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Application.Subscriptions;

/// <summary>Creates a plan. It starts active and public; prices are added separately.</summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record CreatePlanCommand(
    string Code,
    string Name,
    string? Description,
    int TrialDays,
    int? MaxMembers,
    int? MaxTrainers,
    int? MaxStaff,
    long? StorageLimitMb,
    int SortOrder) : ICommand<Result<PlanDetail>>;

/// <summary>
/// Changes a plan's details. <c>Code</c> is absent by design — it is immutable after creation
/// (<see cref="SubscriptionPlan.Code"/>).
/// </summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record UpdatePlanCommand(
    Guid PlanId,
    string Name,
    string? Description,
    int TrialDays,
    int? MaxMembers,
    int? MaxTrainers,
    int? MaxStaff,
    long? StorageLimitMb,
    int SortOrder,
    bool IsPublic,
    string? ExpectedRowVersion) : ICommand<Result<PlanDetail>>;

/// <summary>
/// Sets the price for one cycle and currency — adding it, or changing the existing one.
/// </summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record SetPlanPriceCommand(
    Guid PlanId,
    string BillingCycle,
    string Currency,
    decimal Amount,
    string? ExpectedRowVersion) : ICommand<Result<PlanDetail>>;

[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record RemovePlanPriceCommand(
    Guid PlanId,
    Guid PlanPriceId,
    string? ExpectedRowVersion) : ICommand<Result<PlanDetail>>;

/// <summary>
/// Withdraws the plan from the catalogue — the closest thing to a delete that exists here, because
/// subscriptions and invoices reference plans (§3.4).
/// </summary>
[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record DeactivatePlanCommand(Guid PlanId, string? ExpectedRowVersion)
    : ICommand<Result<PlanDetail>>;

[RequiresPermission(PlatformPermissions.BillingManage)]
public sealed record ActivatePlanCommand(Guid PlanId, string? ExpectedRowVersion)
    : ICommand<Result<PlanDetail>>;

internal sealed class CreatePlanCommandHandler : ICommandHandler<CreatePlanCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public CreatePlanCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        CreatePlanCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string code = (command.Code ?? string.Empty).Trim().ToUpperInvariant();

        // Checked here as well as by the unique index: a typed conflict is a better answer than a
        // database error, and the index remains the guarantee under a race.
        if (await _plans.FindByCodeAsync(code, cancellationToken) is not null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.CodeTaken(code));
        }

        SubscriptionPlan plan;
        try
        {
            plan = SubscriptionPlan.Create(
                code,
                command.Name,
                command.Description ?? string.Empty,
                command.TrialDays,
                new PlanLimits(
                    command.MaxMembers,
                    command.MaxTrainers,
                    command.MaxStaff,
                    command.StorageLimitMb),
                command.SortOrder);
        }
        catch (BusinessRuleViolationException violation)
        {
            return Result.Failure<PlanDetail>(Error.Validation(violation.Code!, violation.Message));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<PlanDetail>(Error.Validation("plans.invalid_input", exception.Message));
        }

        _plans.Add(plan);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, plan.Id, cancellationToken);
    }
}

internal sealed class UpdatePlanCommandHandler : ICommandHandler<UpdatePlanCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public UpdatePlanCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        UpdatePlanCommand command,
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

        try
        {
            plan.UpdateDetails(
                command.Name,
                command.Description ?? string.Empty,
                command.TrialDays,
                new PlanLimits(
                    command.MaxMembers,
                    command.MaxTrainers,
                    command.MaxStaff,
                    command.StorageLimitMb),
                command.SortOrder,
                command.IsPublic);
        }
        catch (BusinessRuleViolationException violation)
        {
            return Result.Failure<PlanDetail>(Error.Validation(violation.Code!, violation.Message));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}

internal sealed class SetPlanPriceCommandHandler
    : ICommandHandler<SetPlanPriceCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public SetPlanPriceCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        SetPlanPriceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Result<BillingCycle> cycle = PlanWrites.ParseBillingCycle(command.BillingCycle);
        if (cycle.IsFailure)
        {
            return Result.Failure<PlanDetail>(cycle.Error!);
        }

        Result<Currency> currency = PlanWrites.ParseCurrency(command.Currency);
        if (currency.IsFailure)
        {
            return Result.Failure<PlanDetail>(currency.Error!);
        }

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.NotFound(command.PlanId));
        }

        Result version = PlanWrites.ApplyExpectedVersion(
            _plans, plan, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<PlanDetail>(version.Error!);
        }

        try
        {
            // Money.Of rounds to the currency's minor units, so a price of 19.999 becomes 20.00 for EUR
            // and 20 for JPY — the amount that can actually be charged (AD-42).
            plan.SetPrice(cycle.Value, Money.Of(command.Amount, currency.Value));
        }
        catch (BusinessRuleViolationException violation)
        {
            return Result.Failure<PlanDetail>(Error.Validation(violation.Code!, violation.Message));
        }

        // A price row changed, not the plan's own columns, so the plan's version has to be advanced
        // explicitly or two concurrent price edits would both succeed.
        _plans.MarkAggregateChanged(plan);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}

internal sealed class RemovePlanPriceCommandHandler
    : ICommandHandler<RemovePlanPriceCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public RemovePlanPriceCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        RemovePlanPriceCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.NotFound(command.PlanId));
        }

        Result version = PlanWrites.ApplyExpectedVersion(
            _plans, plan, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<PlanDetail>(version.Error!);
        }

        try
        {
            plan.RemovePrice(command.PlanPriceId);
        }
        catch (DomainException)
        {
            return Result.Failure<PlanDetail>(PlanErrors.PriceNotFound);
        }

        _plans.MarkAggregateChanged(plan);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}

internal sealed class DeactivatePlanCommandHandler
    : ICommandHandler<DeactivatePlanCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public DeactivatePlanCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        DeactivatePlanCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.NotFound(command.PlanId));
        }

        if (!plan.IsActive)
        {
            return Result.Failure<PlanDetail>(PlanErrors.AlreadyInactive);
        }

        Result version = PlanWrites.ApplyExpectedVersion(
            _plans, plan, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<PlanDetail>(version.Error!);
        }

        plan.Deactivate();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}

internal sealed class ActivatePlanCommandHandler
    : ICommandHandler<ActivatePlanCommand, Result<PlanDetail>>
{
    private readonly IPlanRepository _plans;
    private readonly IPlanQueries _planQueries;
    private readonly IUnitOfWork _unitOfWork;

    public ActivatePlanCommandHandler(
        IPlanRepository plans,
        IPlanQueries planQueries,
        IUnitOfWork unitOfWork)
    {
        _plans = plans;
        _planQueries = planQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<PlanDetail>> HandleAsync(
        ActivatePlanCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        SubscriptionPlan? plan = await _plans.FindByIdAsync(command.PlanId, cancellationToken);
        if (plan is null)
        {
            return Result.Failure<PlanDetail>(PlanErrors.NotFound(command.PlanId));
        }

        if (plan.IsActive)
        {
            return Result.Failure<PlanDetail>(PlanErrors.AlreadyActive);
        }

        Result version = PlanWrites.ApplyExpectedVersion(
            _plans, plan, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<PlanDetail>(version.Error!);
        }

        plan.Activate();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await PlanWrites.ReadBackAsync(_planQueries, command.PlanId, cancellationToken);
    }
}
