using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings;

/// <summary>Stable error codes for the settings surface (M7 spec §4). Grows with the write and
/// validation handlers in later tasks.</summary>
public static class SettingErrors
{
    /// <summary>The provider could not resolve an effective view — a reload failed with no previous
    /// snapshot to fall back on.</summary>
    public static Error InvalidState(string message) => Error.Validation("settings.invalid_state", message);

    /// <summary>A write named a key the catalog does not declare.</summary>
    public static Error UnknownKey(string key) =>
        Error.Validation("settings.unknown_key", $"No setting named '{key}' is defined.");

    /// <summary>A write named a key that belongs to a different category than the one being written.</summary>
    public static Error WrongCategory(string key, SettingCategory category) =>
        Error.Validation("settings.wrong_category", $"'{key}' does not belong to the {category} category.");

    /// <summary>A write's value fails the key's own type or range rule.</summary>
    public static Error InvalidValue(string key, string rule) =>
        Error.Validation("settings.invalid_value", $"`{key}` {rule}.");

    /// <summary>The same key appears more than once in one write batch.</summary>
    public static Error DuplicateKey(string key) =>
        Error.Validation("settings.duplicate_key", $"'{key}' appears more than once in the batch.");

    /// <summary>The GET or PUT named a category the catalog does not declare.</summary>
    public static Error UnknownCategory(string category) =>
        Error.Validation("settings.unknown_category", $"'{category}' is not a known setting category.");

    /// <summary>Maintenance settings change only through the dedicated, confirmed endpoint.</summary>
    public static Error UseMaintenanceEndpoint { get; } = Error.Validation(
        "settings.use_maintenance_endpoint",
        "Maintenance settings change through POST /settings/maintenance-mode, which requires confirmation.");

    /// <summary>An SMTP connectivity test was requested while email is disabled.</summary>
    public static Error EmailDisabled { get; } = Error.Validation(
        "settings.email_disabled",
        "Email is disabled; enable it and save before testing the connection.");

    /// <summary>A maintenance-mode change was requested without the confirmation header.</summary>
    public static Error ConfirmationRequired { get; } = Error.Validation(
        "settings.confirmation_required",
        "Send X-Confirm-Action: maintenance to change maintenance mode.");

    /// <summary>A maintenance-mode change carried the confirmation header with the wrong value.</summary>
    public static Error ConfirmationMismatch { get; } = Error.Validation(
        "settings.confirmation_mismatch",
        "X-Confirm-Action must be exactly 'maintenance'.");
}
