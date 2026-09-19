using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings;

/// <summary>Every declared setting, optionally narrowed to one category, with its effective value,
/// source and — for a category whose current overlay fails cross-field validation — why (M7 spec
/// §4). Sensitive values come back masked; a caller with only <c>settings.read</c> never sees one.</summary>
[RequiresPermission(PlatformPermissions.SettingsRead)]
public sealed record GetSettingsQuery(SettingCategory? Category) : IQuery<Result<IReadOnlyList<SettingView>>>;

internal sealed class GetSettingsQueryHandler : IQueryHandler<GetSettingsQuery, Result<IReadOnlyList<SettingView>>>
{
    private readonly ISettingsProvider _provider;
    private readonly ISettingsValidator _validator;

    public GetSettingsQueryHandler(ISettingsProvider provider, ISettingsValidator validator)
    {
        _provider = provider;
        _validator = validator;
    }

    public async Task<Result<IReadOnlyList<SettingView>>> HandleAsync(GetSettingsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<EffectiveSetting> effective = await _provider.GetEffectiveAsync(cancellationToken);
        if (query.Category is SettingCategory category)
        {
            effective = effective.Where(e => e.Definition.Category == category).ToList();
        }

        IReadOnlyDictionary<string, string?> overlay = await _provider.GetOverlayAsync(cancellationToken);

        // One validation per distinct category present, so a broken category is flagged on every one
        // of its rows without re-validating the same category once per key.
        var messageByCategory = new Dictionary<SettingCategory, string>();
        foreach (SettingCategory distinctCategory in effective.Select(e => e.Definition.Category).Distinct())
        {
            Result validation = await _validator.ValidateAsync(distinctCategory, overlay, cancellationToken);
            if (validation.IsFailure)
            {
                messageByCategory[distinctCategory] = validation.Error!.Message;
            }
        }

        return Result.Success<IReadOnlyList<SettingView>>(effective
            .Select(e => messageByCategory.TryGetValue(e.Definition.Category, out string? message)
                ? SettingViews.From(e, valid: false, validationMessage: message)
                : SettingViews.From(e))
            .ToList());
    }
}
