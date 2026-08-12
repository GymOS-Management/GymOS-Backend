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
/// Replaces a gym's branding. Declarative, like the aggregate: the caller sends the branding they want and
/// omitted values are cleared, so two editors converge on a chosen state instead of composing into one
/// neither picked.
/// </summary>
/// <param name="ExpectedRowVersion">
/// From <c>If-Match</c>, and required — this is an overwrite of what the caller read. The version is the
/// *gym's*, because branding is presented as part of the gym and there is one ETag for the pair.
/// </param>
[RequiresPermission(PlatformPermissions.GymsUpdate)]
public sealed record UpdateGymBrandingCommand(
    Guid GymId,
    string? LogoUrl,
    string? FaviconUrl,
    string? PrimaryColour,
    string? SecondaryColour,
    string? CustomDomain,
    string? EmailSenderName,
    string? ExpectedRowVersion) : ICommand<Result<GymDetail>>;

internal sealed class UpdateGymBrandingCommandHandler
    : ICommandHandler<UpdateGymBrandingCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IPermissionEvaluator _permissionEvaluator;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateGymBrandingCommandHandler(
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
        UpdateGymBrandingCommand command,
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

        GymBranding? branding = await _gyms.FindBrandingAsync(command.GymId, cancellationToken);
        if (branding is null)
        {
            // Provisioning creates the record alongside the gym, so this is not a state a caller can
            // reach — it means a gym predates that guarantee. Creating it is a better answer than a 500.
            branding = GymBranding.CreateEmpty(command.GymId);
            _gyms.AddBranding(branding);
        }

        Result version = GymWrites.ApplyExpectedVersion(
            _gyms, gym, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<GymDetail>(version.Error!);
        }

        string? customDomain = string.IsNullOrWhiteSpace(command.CustomDomain)
            ? null
            : command.CustomDomain.Trim().ToLowerInvariant();

        // A domain must resolve to exactly one gym. Checked before the write so the caller gets a typed
        // conflict; the unique index remains the guarantee under a race.
        if (customDomain is not null
            && await _gyms.CustomDomainTakenAsync(customDomain, command.GymId, cancellationToken))
        {
            return Result.Failure<GymDetail>(GymErrors.CustomDomainTaken(customDomain));
        }

        try
        {
            branding.Update(
                command.LogoUrl,
                command.FaviconUrl,
                command.PrimaryColour,
                command.SecondaryColour,
                customDomain,
                command.EmailSenderName);
        }
        catch (BusinessRuleViolationException violation)
        {
            return Result.Failure<GymDetail>(Error.Validation(violation.Code!, violation.Message));
        }

        // The branding row changed, not the gym's — so the gym's version has to be advanced explicitly,
        // or the ETag the caller just satisfied would still be current and the next concurrent branding
        // edit would overwrite this one unnoticed.
        _gyms.MarkAggregateChanged(gym);

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, _permissionEvaluator, command.GymId, cancellationToken);
    }
}
