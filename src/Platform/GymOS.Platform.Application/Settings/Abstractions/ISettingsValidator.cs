using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Cross-field validation for one category's write, over and above each definition's own
/// <see cref="SettingDefinition.ValidateValue"/> (M7 spec §4) — e.g. a lockout duration that must not
/// exceed a related maximum. Implemented in Task 5.</summary>
public interface ISettingsValidator
{
    Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken ct);
}
