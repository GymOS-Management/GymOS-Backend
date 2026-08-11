using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Features.Abstractions;
using GymOS.Platform.Domain.Features;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Features;

/// <summary>The catalogue, with how widely each feature is actually live (§3.5).</summary>
[RequiresPermission(PlatformPermissions.FeaturesRead)]
public sealed record ListFeaturesQuery : IQuery<Result<IReadOnlyList<FeatureListItem>>>;

/// <summary>
/// Edits what an operator owns about a feature: its copy, its platform default, and whether it is
/// sold (§3.5). <b>The key is not editable</b> — code branches on it.
/// </summary>
/// <param name="ExpectedRowVersion">
/// <c>If-Match</c>, <b>required</b>. This overwrites what the caller read, and flipping
/// <c>DefaultEnabled</c> re-answers the feature for every gym that never decided for itself — the
/// widest blast radius of any write in this module. The M3.1 price endpoints taught this lesson the
/// expensive way; here it is required from the start.
/// </param>
[RequiresPermission(PlatformPermissions.FeaturesManage)]
public sealed record UpdateFeatureCommand(
    Guid FeatureId,
    string Name,
    string? Description,
    string? Category,
    bool DefaultEnabled,
    bool IsPremium,
    string? ExpectedRowVersion) : ICommand<Result<FeatureListItem>>;

internal sealed class ListFeaturesQueryHandler
    : IQueryHandler<ListFeaturesQuery, Result<IReadOnlyList<FeatureListItem>>>
{
    private readonly IFeatureQueries _featureQueries;

    public ListFeaturesQueryHandler(IFeatureQueries featureQueries) => _featureQueries = featureQueries;

    public async Task<Result<IReadOnlyList<FeatureListItem>>> HandleAsync(
        ListFeaturesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return Result.Success(await _featureQueries.ListCatalogueAsync(cancellationToken));
    }
}

internal sealed class UpdateFeatureCommandHandler
    : ICommandHandler<UpdateFeatureCommand, Result<FeatureListItem>>
{
    private readonly IFeatureRepository _features;
    private readonly IFeatureQueries _featureQueries;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateFeatureCommandHandler(
        IFeatureRepository features,
        IFeatureQueries featureQueries,
        IUnitOfWork unitOfWork)
    {
        _features = features;
        _featureQueries = featureQueries;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<FeatureListItem>> HandleAsync(
        UpdateFeatureCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        Feature? feature = await _features.FindByIdAsync(command.FeatureId, cancellationToken);
        if (feature is null)
        {
            return Result.Failure<FeatureListItem>(FeatureErrors.NotFound(command.FeatureId));
        }

        Result version = FeatureWrites.ApplyExpectedVersion(
            _features, feature, command.ExpectedRowVersion, versionRequired: true);
        if (version.IsFailure)
        {
            return Result.Failure<FeatureListItem>(version.Error!);
        }

        try
        {
            feature.Describe(command.Name, command.Description, command.Category);
            feature.SetDefault(command.DefaultEnabled);
            feature.SetPremium(command.IsPremium);
        }
        catch (DomainException exception)
        {
            return Result.Failure<FeatureListItem>(
                Error.Validation("features.invalid", exception.Message));
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        FeatureListItem? updated = await _featureQueries.GetAsync(feature.Id, cancellationToken);

        return updated is null
            ? Result.Failure<FeatureListItem>(FeatureErrors.NotFound(feature.Id))
            : Result.Success(updated);
    }
}

/// <summary>Failure vocabulary of the feature surface. Codes are stable API.</summary>
public static class FeatureErrors
{
    public static Error NotFound(Guid featureId) => Error.NotFound(
        "features.not_found",
        $"Feature '{featureId}' does not exist. The catalogue is seeded — keys cannot be created.");

    public static Error UnknownKey(string key) => Error.Validation(
        "features.unknown_key",
        $"'{key}' is not a feature in this platform's catalogue.");

    public static Error VersionRequired => Error.Conflict(
        "concurrency.version_required",
        "This update requires an If-Match header carrying the rowVersion you read. " +
        "Re-read the feature and send its version.");

    public static Error MalformedVersion => Error.Validation(
        "concurrency.malformed_version",
        "The If-Match header is not a version token issued by this API. " +
        "Send the rowVersion from the resource, quoted, without a weak (W/) prefix.");
}

/// <summary>The precondition every feature write shares (<c>SubscriptionWrites</c>' shape).</summary>
internal static class FeatureWrites
{
    public static Result ApplyExpectedVersion(
        IFeatureRepository features,
        Feature feature,
        string? expectedVersion,
        bool versionRequired)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return versionRequired ? Result.Failure(FeatureErrors.VersionRequired) : Result.Success();
        }

        if (!RowVersion.TryParse(expectedVersion, out uint parsed))
        {
            return Result.Failure(FeatureErrors.MalformedVersion);
        }

        features.ExpectVersion(feature, parsed);
        return Result.Success();
    }
}
