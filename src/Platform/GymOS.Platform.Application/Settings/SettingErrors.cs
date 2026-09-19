using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.Settings;

/// <summary>Stable error codes for the settings surface (M7 spec §4). Grows with the write and
/// validation handlers in later tasks; only the state member this task needs exists so far.</summary>
public static class SettingErrors
{
    /// <summary>The provider could not resolve an effective view — a reload failed with no previous
    /// snapshot to fall back on.</summary>
    public static Error InvalidState(string message) => Error.Validation("settings.invalid_state", message);
}
