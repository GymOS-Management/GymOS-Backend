using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Domain.Gyms;
using GymOS.Platform.Domain.Gyms.Rules;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;
using GymOS.SharedKernel.Rules;

namespace GymOS.Platform.Application.Gyms;

/// <summary>
/// Creates a gym in <c>Provisioning</c> (ARCHITECTURE.md §3.3).
///
/// <para>The gym is not live yet. Provisioning exists so a tenant can be set up — subscription assigned,
/// branding uploaded, staff invited — before members can reach it, and <c>activate</c> is the deliberate
/// moment it opens. When subscription assignment lands in M3 it becomes the natural completion of
/// provisioning; until then activation is an explicit operator action.</para>
///
/// <para>Locale values are required with no defaults: the approved design says they come from platform
/// settings, and a default buried in the domain would be a hidden policy every gym inherited.</para>
/// </summary>
[RequiresPermission(PlatformPermissions.GymsCreate)]
public sealed record ProvisionGymCommand(
    string Slug,
    string Name,
    string OwnerName,
    string OwnerEmail,
    string? Phone,
    string CountryCode,
    string TimeZoneId,
    string Currency) : ICommand<Result<GymDetail>>;

internal sealed class ProvisionGymCommandHandler : ICommandHandler<ProvisionGymCommand, Result<GymDetail>>
{
    private readonly IGymRepository _gyms;
    private readonly IGymQueries _gymQueries;
    private readonly IUnitOfWork _unitOfWork;

    public ProvisionGymCommandHandler(
        IGymRepository gyms,
        IGymQueries gymQueries,
        IUnitOfWork unitOfWork)
    {
        _gyms = gyms;
        _gymQueries = gymQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<GymDetail>> HandleAsync(
        ProvisionGymCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string slug = (command.Slug ?? string.Empty).Trim();

        // Validated before the uniqueness check so a malformed slug reports what is wrong with it rather
        // than "not taken" followed by a domain exception.
        Result shape = BusinessRules.Validate(new GymSlugMustBeUrlSafe(slug));
        if (shape.IsFailure)
        {
            return Result.Failure<GymDetail>(shape.Error!);
        }

        // Checked here as well as by the unique index: a typed conflict is a better answer than a
        // database error, and the index remains the actual guarantee under a race. The lookup includes
        // deleted gyms — a slug is never released, because it may still be in a member's bookmark.
        if (await _gyms.FindBySlugAsync(slug, cancellationToken) is not null)
        {
            return Result.Failure<GymDetail>(GymErrors.SlugTaken(slug));
        }

        Gym gym;
        try
        {
            gym = Gym.Provision(
                slug,
                command.Name,
                new GymOwnerContact(command.OwnerName ?? string.Empty, command.OwnerEmail ?? string.Empty, command.Phone),
                GymLocale.Of(command.CountryCode, command.TimeZoneId, command.Currency));
        }
        catch (BusinessRuleViolationException violation)
        {
            // A broken rule is reported to the caller as validation rather than surfacing as a 500 — the
            // rule's own code and message travel with it.
            return Result.Failure<GymDetail>(Error.Validation(violation.Code!, violation.Message));
        }
        catch (ArgumentException exception)
        {
            return Result.Failure<GymDetail>(Error.Validation("gyms.invalid_input", exception.Message));
        }

        _gyms.Add(gym);

        // Created alongside the gym so the detail view and the branding endpoint always have a record to
        // read and write, rather than every caller needing a create-or-update path.
        _gyms.AddBranding(GymBranding.CreateEmpty(gym.Id));

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return await GymWrites.ReadBackAsync(_gymQueries, gym.Id, cancellationToken);
    }
}
