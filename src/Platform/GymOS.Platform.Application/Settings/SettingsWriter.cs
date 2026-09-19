using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings;

/// <summary>The view the console reads for one setting: the catalog's shape plus the value in
/// effect, with a sensitive value masked (M7 spec §3).</summary>
public static class SettingViews
{
    public static SettingView From(EffectiveSetting effective, bool valid = true, string? validationMessage = null) =>
        new(
            effective.Definition.Key,
            effective.Definition.Category.ToString(),
            effective.Definition.ValueType.ToString(),
            effective.Definition.IsSensitive,
            effective.Definition.Description,
            effective.Definition.IsSensitive && effective.Value is not null ? PlatformSettingDefinitions.Mask : effective.Value,
            effective.Source.ToString(),
            valid,
            validationMessage,
            effective.UpdatedBy,
            effective.UpdatedAtUtc);
}

/// <summary>
/// Validates and applies one category's batch of setting writes (M7 spec §4), then hands back the
/// category's fresh views. Shared by <see cref="UpdateSettingsCommand"/>'s handler and Task 9's
/// maintenance-mode command — both write rows the same way; only who may call and what extra
/// confirmation they need differs.
///
/// Order matters: every key and value in the batch is checked before any row is touched (a partially
/// applied batch would leave settings in a state nobody asked for), then the would-be effect on the
/// bound options is checked by <see cref="ISettingsValidator"/> before the batch is written, so a
/// caller who breaks cross-field state gets refused with nothing changed.
/// </summary>
public sealed class SettingsWriter
{
    private readonly ISettingsRepository _rows;
    private readonly ISettingsProvider _provider;
    private readonly ISettingProtector _protector;
    private readonly ISettingsValidator _validator;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;

    public SettingsWriter(
        ISettingsRepository rows,
        ISettingsProvider provider,
        ISettingProtector protector,
        ISettingsValidator validator,
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser)
    {
        _rows = rows;
        _provider = provider;
        _protector = protector;
        _validator = validator;
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
    }

    public async Task<Result<IReadOnlyList<SettingView>>> ApplyAsync(
        SettingCategory category, IReadOnlyList<SettingValueWrite> values, CancellationToken ct)
    {
        // 0: no key may appear twice in one batch — before anything else, ordinal comparison.
        // ISettingsRepository.FindByKeyAsync queries the database, not the change tracker, so a
        // second occurrence of the same key would insert a second row rather than update the
        // first, and trip the unique index instead of failing cleanly.
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (SettingValueWrite write in values)
        {
            if (!seenKeys.Add(write.Key))
            {
                return Fail(SettingErrors.DuplicateKey(write.Key));
            }
        }

        // 1-2: definitions, categories, types and ranges — before any row is touched.
        var resolved = new List<(SettingDefinition Definition, string? Value)>();
        foreach (SettingValueWrite write in values)
        {
            // A null or whitespace key names nothing the catalog could ever declare, and
            // PlatformSettingDefinitions.TryGet's dictionary lookup throws on a null key rather than
            // just failing to find it — so this is checked first, and reported the same way an unknown
            // key is.
            if (string.IsNullOrWhiteSpace(write.Key))
            {
                return Fail(SettingErrors.UnknownKey(write.Key ?? string.Empty));
            }

            if (!PlatformSettingDefinitions.TryGet(write.Key, out SettingDefinition definition))
            {
                return Fail(SettingErrors.UnknownKey(write.Key));
            }

            if (definition.Category != category)
            {
                return Fail(SettingErrors.WrongCategory(write.Key, category));
            }

            if (write.Value is not null && write.Value.Length > 0 && !(definition.IsSensitive && write.Value == PlatformSettingDefinitions.Mask))
            {
                string? rule = definition.ValidateValue(write.Value);
                if (rule is not null)
                {
                    return Fail(SettingErrors.InvalidValue(write.Key, rule));
                }
            }

            resolved.Add((definition, write.Value));
        }

        // 5: the would-be overlay — current database rows with this batch applied — through the options validators.
        IReadOnlyList<EffectiveSetting> current = await _provider.GetEffectiveAsync(ct);
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (EffectiveSetting e in current.Where(e => e.Source == SettingSource.Database && e.Definition.ConfigurationPath is not null))
        {
            overlay[e.Definition.ConfigurationPath!] = e.Definition.ToConfigurationValue(e.Value!);
        }

        foreach ((SettingDefinition d, string? v) in resolved.Where(r => r.Definition.ConfigurationPath is not null))
        {
            if (d.IsSensitive && v == PlatformSettingDefinitions.Mask)
            {
                continue; // unchanged
            }

            if (string.IsNullOrEmpty(v))
            {
                overlay.Remove(d.ConfigurationPath!); // back to the layer below
            }
            else
            {
                overlay[d.ConfigurationPath!] = d.ToConfigurationValue(v);
            }
        }

        Result validation = await _validator.ValidateAsync(category, overlay, ct);
        if (validation.IsFailure)
        {
            return Fail(validation.Error!);
        }

        // 3-4, 6: rows.
        foreach ((SettingDefinition d, string? v) in resolved)
        {
            PlatformSetting? row = await _rows.FindByKeyAsync(d.Key, ct);
            if (d.IsSensitive && v == PlatformSettingDefinitions.Mask)
            {
                continue;
            }

            if (string.IsNullOrEmpty(v))
            {
                if (row is not null)
                {
                    _rows.Remove(row);
                }

                continue;
            }

            string? plain = d.IsSensitive ? null : v;
            string? protectedValue = d.IsSensitive ? _protector.Protect(d.Key, v) : null;
            if (row is null)
            {
                _rows.Add(PlatformSetting.Create(Guid.NewGuid(), d, plain, protectedValue, _currentUser.UserId));
            }
            else
            {
                row.Set(plain, protectedValue, _currentUser.UserId);
            }
        }

        await _unitOfWork.SaveChangesAsync(ct); // ConcurrencyConflictException -> 409 by the existing handler
        _provider.Invalidate(); // 7

        IReadOnlyList<EffectiveSetting> after = await _provider.GetEffectiveAsync(ct);
        return Result.Success<IReadOnlyList<SettingView>>(
            after.Where(e => e.Definition.Category == category).Select(e => SettingViews.From(e)).ToList());
    }

    private static Result<IReadOnlyList<SettingView>> Fail(Error error) => Result.Failure<IReadOnlyList<SettingView>>(error);
}
