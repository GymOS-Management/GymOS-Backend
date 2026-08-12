using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Subscriptions;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Gyms;

// The four status transitions, together, because they share their shape: load, check the current state,
// honour an optional precondition, transition, read back. Keeping them in one file makes it obvious when
// one of them is missing a step the others have.

/// <summary>Opens a provisioned gym for business.</summary>
[RequiresPermission(PlatformPermissions.GymsUpdate)]
public sealed record ActivateGymCommand(Guid GymId, string? ExpectedRowVersion)
    : ICommand<Result<GymDetail>>;

/// <summary>
/// Withholds service from a gym. Reversible, and the reason is recorded and returned by the API — an
/// operator reactivating next week needs to know why it happened.
/// </summary>
[RequiresPermission(PlatformPermissions.GymsSuspend)]
public sealed record SuspendGymCommand(Guid GymId, string Reason, string? ExpectedRowVersion)
    : ICommand<Result<GymDetail>>;

[RequiresPermission(PlatformPermissions.GymsSuspend)]
public sealed record ReactivateGymCommand(Guid GymId, string? ExpectedRowVersion)
    : ICommand<Result<GymDetail>>;

/// <summary>
/// Soft-deletes a gym (ARCHITECTURE.md §3.3, AD-6). The row survives the retention window; a purge job
/// performs the hard delete.
///
/// <para>Returns nothing, unlike the other lifecycle commands: <c>DELETE</c> is <c>204</c> by convention
/// (API_STANDARDS.md §2), and a caller who wants to see the recorded deletion can <c>GET</c> the gym —
/// which still resolves, deliberately, so "deleted" stays distinguishable from "never existed".</para>
/// </summary>
/// <param name="Confirmation">
/// The step-up confirmation from <c>X-Confirm-Action</c>: the caller must name the gym's slug. Deleting a
/// gym removes an entire business's access, so a mis-clicked row in a list must not be able to do it.
/// </param>
[RequiresPermission(PlatformPermissions.GymsDelete)]
public sealed record DeleteGymCommand(Guid GymId, string? Confirmation, string? ExpectedRowVersion)
    : ICommand<Result>;

internal sealed class ActivateGymCommandHandler : ICommandHandler<ActivateGymCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IUnitOfWork _unitOfWork;

    public ActivateGymCommandHandler(
        IGymRepository gyms,
        IGymQueries gymQueries,
        IPermissionEvaluator permissionEvaluator,
        IUnitOfWork unitOfWork)
    {
        _gyms = gyms;
        _gymQueries = gymQueries;
        _permissionEvaluator = permissionEvaluator;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<GymDetail>> HandleAsync(
        ActivateGymCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure<GymDetail>(GymErrors.NotFound(command.GymId));
        }

        if (gym.IsDeleted)
        {
            return Result.Failure<GymDetail>(GymErrors.Deleted);
        }

        if (gym.Status == GymStatus.Active)
        {
            return Result.Failure<GymDetail>(GymErrors.AlreadyActive);
        }

        // Suspended gyms go through reactivate, which is a different permission: lifting a suspension is
        // an operator decision about a dispute, not the same act as finishing setup.
        if (gym.Status == GymStatus.Suspended)
        {
            return Result.Failure<GymDetail>(GymErrors.AlreadySuspended);
        }

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<GymDetail>(version.Error!);
        }

        gym.Activate();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, _permissionEvaluator, command.GymId, cancellationToken);
    }
}

internal sealed class SuspendGymCommandHandler : ICommandHandler<SuspendGymCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IUnitOfWork _unitOfWork;

    public SuspendGymCommandHandler(
        IGymRepository gyms,
        IGymQueries gymQueries,
        IPermissionEvaluator permissionEvaluator,
        IUnitOfWork unitOfWork)
    {
        _gyms = gyms;
        _gymQueries = gymQueries;
        _permissionEvaluator = permissionEvaluator;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<GymDetail>> HandleAsync(
        SuspendGymCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure<GymDetail>(
                Error.Validation("gyms.reason_required", "A suspension reason is required."));
        }

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure<GymDetail>(GymErrors.NotFound(command.GymId));
        }

        if (gym.IsDeleted)
        {
            return Result.Failure<GymDetail>(GymErrors.Deleted);
        }

        if (gym.Status == GymStatus.Suspended)
        {
            return Result.Failure<GymDetail>(GymErrors.AlreadySuspended);
        }

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<GymDetail>(version.Error!);
        }

        gym.Suspend(command.Reason);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, _permissionEvaluator, command.GymId, cancellationToken);
    }
}

internal sealed class ReactivateGymCommandHandler
    : ICommandHandler<ReactivateGymCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IUnitOfWork _unitOfWork;

    public ReactivateGymCommandHandler(
        IGymRepository gyms,
        IGymQueries gymQueries,
        IPermissionEvaluator permissionEvaluator,
        IUnitOfWork unitOfWork)
    {
        _gyms = gyms;
        _gymQueries = gymQueries;
        _permissionEvaluator = permissionEvaluator;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<GymDetail>> HandleAsync(
        ReactivateGymCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure<GymDetail>(GymErrors.NotFound(command.GymId));
        }

        if (gym.IsDeleted)
        {
            return Result.Failure<GymDetail>(GymErrors.Deleted);
        }

        if (gym.Status != GymStatus.Suspended)
        {
            return Result.Failure<GymDetail>(GymErrors.NotSuspended);
        }

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return Result.Failure<GymDetail>(version.Error!);
        }

        gym.Reactivate();
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, _permissionEvaluator, command.GymId, cancellationToken);
    }
}

internal sealed class DeleteGymCommandHandler : ICommandHandler<DeleteGymCommand, Result>
{
    private readonly IGymRepository _gyms;
    private readonly ISubscriptionRepository _subscriptions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public DeleteGymCommandHandler(
        IGymRepository gyms,
        ISubscriptionRepository subscriptions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _gyms = gyms;
        _subscriptions = subscriptions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<Result> HandleAsync(DeleteGymCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Gym? gym = await _gyms.FindByIdAsync(command.GymId, cancellationToken);
        if (gym is null)
        {
            return Result.Failure(GymErrors.NotFound(command.GymId));
        }

        // Idempotent rather than an error: an operator who deleted a gym and retried should be told it is
        // done, not shown a failure for having succeeded. The confirmation is still not asked for twice.
        if (gym.IsDeleted)
        {
            return Result.Success();
        }

        // Confirmation before the version check: "you must name the gym" is more useful to the caller than
        // "your copy is stale" when both are true.
        Result confirmation = GymWrites.Confirm(gym, command.Confirmation);
        if (confirmation.IsFailure)
        {
            return confirmation;
        }

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: false);
        if (version.IsFailure)
        {
            return version;
        }

        DateTimeOffset now = _clock.UtcNow;
        gym.Delete(now);

        // A deleted gym must stop costing money. Assignment already refuses a deleted gym
        // (SubscriptionErrors.GymDeleted), but nothing enforced the same intent on this side, so
        // the renewal sweep kept advancing the period and issuing invoices against a business
        // that no longer exists — billing, and reporting as receivable, a deleted tenant forever.
        // Cancelled in the same transaction as the deletion: two separate commits could leave a
        // deleted gym holding a live subscription.
        GymSubscription? subscription = await _subscriptions.FindLiveByGymAsync(
            command.GymId, cancellationToken);
        if (subscription is not null)
        {
            // Immediately, not at the period end: there is nothing left to deliver service to,
            // and a scheduled cancellation would be applied by a sweep that must no longer see
            // this gym at all. Invoices already issued stay — they are the billing record.
            subscription.CancelNow(RevocationReasonGymDeleted, now);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>Recorded as the cancellation reason, so the history says why it ended.</summary>
    private const string RevocationReasonGymDeleted = "The gym was deleted.";
}
