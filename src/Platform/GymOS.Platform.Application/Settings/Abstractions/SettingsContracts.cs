using GymOS.Platform.Domain.Settings;

namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Where an effective setting's value came from, in resolution order (M7 spec §3): a stored
/// row beats configuration, which beats the definition's own default.</summary>
public enum SettingSource
{
    Default = 1,
    Configuration = 2,
    Database = 3,
}

/// <summary>A setting as the console reads it: the catalog's shape plus the value actually in effect
/// and where it came from, and — for a value that fails its own definition's rule — why.</summary>
public sealed record SettingView(
    string Key,
    SettingCategory Category,
    SettingValueType ValueType,
    bool IsSensitive,
    string Description,
    string? Value,
    SettingSource Source,
    bool Valid,
    string? ValidationMessage,
    Guid? UpdatedBy,
    DateTimeOffset? UpdatedAtUtc);

/// <summary>One key/value pair of a write request. <c>Value</c> null clears the row back to
/// configuration/default; for a sensitive key, <see cref="PlatformSettingDefinitions.Mask"/> means
/// "leave the stored value unchanged".</summary>
public sealed record SettingValueWrite(string Key, string? Value);

/// <summary>The outcome of an SMTP connectivity probe (Task 8), reported by the stage it reached.</summary>
public sealed record SmtpConnectionTestResult(bool Ok, string? Stage, string? Server, string? Error);

/// <summary>One definition resolved against its row, configuration and default (M7 spec §3) — what
/// <see cref="ISettingsProvider"/> hands the rest of the platform to read by.</summary>
public sealed record EffectiveSetting(
    SettingDefinition Definition,
    string? Value,
    SettingSource Source,
    Guid? UpdatedBy,
    DateTimeOffset? UpdatedAtUtc);
