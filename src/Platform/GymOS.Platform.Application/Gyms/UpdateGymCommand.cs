using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Application.Gyms;

/// <summary>
/// Changes a gym's details. Slug is absent by design — it is immutable after creation
/// (<see cref="Gym.Slug"/>).
/// </summary>
/// <param name="ExpectedRowVersion">
/// From <c>If-Match</c>. Required: this overwrites fields the caller read.
/// </param>
[RequiresPermission(PlatformPermissions.GymsUpdate)]
public sealed record UpdateGymCommand(
    Guid GymId,
    string Name,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    string CountryCode,
    string TimeZoneId,
    string Currency,
    string? ExpectedRowVersion) : ICommand<Result<GymDetail>>;

internal sealed class UpdateGymCommandHandler : ICommandHandler<UpdateGymCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateGymCommandHandler(
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
        UpdateGymCommand command,
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

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<GymDetail>(version.Error!);
        }

        try
        {
            gym.UpdateDetails(
                command.Name,
                new GymOwnerContact(command.OwnerName ?? string.Empty, command.OwnerEmail ?? string.Empty, command.Phone),
                GymLocale.Of(command.CountryCode, command.TimeZoneId, command.Currency));
        }
        catch (BusinessRuleViolationException violation)
        {
            return Result.Failure<GymDetail>(Error.Validation(violation.Code!, violation.Message));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<GymDetail>(Error.Validation("gyms.invalid_input", exception.Message));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, _permissionEvaluator, command.GymId, cancellationToken);
    }
}
