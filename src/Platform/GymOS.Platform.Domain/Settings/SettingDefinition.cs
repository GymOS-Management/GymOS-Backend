using System.Globalization;
using System.Text.Json;

namespace GymOS.Platform.Domain.Settings;

/// <summary>
/// One declared setting (M7 spec §2). Definitions are code; rows are values. A value without a
/// definition cannot be written, and the definition is what a console renders — type, range,
/// description — whether or not a row exists.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingCategory Category,
    SettingValueType ValueType,
    bool IsSensitive,
    string Description,
    string? Default,
    string? ConfigurationPath,
    SettingDurationUnit Unit = SettingDurationUnit.None,
    int? Min = null,
    int? Max = null)
{
    /// <summary>The rule a value breaks, or null when it is valid for this definition.</summary>
    public string? ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        switch (ValueType)
        {
            case SettingValueType.Int:
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                {
                    return "must be an integer";
                }

                if ((Min is int min && number < min) || (Max is int max && number > max))
                {
                    return $"must be between {Min ?? int.MinValue} and {Max ?? int.MaxValue}";
                }

                return null;

            case SettingValueType.Bool:
                return bool.TryParse(value, out _) ? null : "must be true or false";

            case SettingValueType.Json:
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(value);
                    return null;
                }
                catch (JsonException)
                {
                    return "must be valid JSON";
                }

            default:
                return null;
        }
    }

    /// <summary>
    /// The text to place at <see cref="ConfigurationPath"/>: a duration setting stored as a whole
    /// number becomes the TimeSpan string the options property binds, so "15" for a minutes key
    /// overlays as "00:15:00" rather than binding as fifteen days.
    /// </summary>
    public string? ToConfigurationValue(string value)
    {
        if (Unit == SettingDurationUnit.None || ValueType != SettingValueType.Int)
        {
            return value;
        }

        int amount = int.Parse(value, CultureInfo.InvariantCulture);
        TimeSpan span = Unit switch
        {
            SettingDurationUnit.Seconds => TimeSpan.FromSeconds(amount),
            SettingDurationUnit.Minutes => TimeSpan.FromMinutes(amount),
            SettingDurationUnit.Hours => TimeSpan.FromHours(amount),
            _ => TimeSpan.FromDays(amount),
        };

        return span.ToString("c", CultureInfo.InvariantCulture);
    }

    /// <summary>The inverse of <see cref="ToConfigurationValue"/>, for showing a configuration-sourced duration in the key's unit.</summary>
    public string? FromConfigurationValue(string? raw)
    {
        if (raw is null || Unit == SettingDurationUnit.None || ValueType != SettingValueType.Int)
        {
            return raw;
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan span))
        {
            return raw;
        }

        double amount = Unit switch
        {
            SettingDurationUnit.Seconds => span.TotalSeconds,
            SettingDurationUnit.Minutes => span.TotalMinutes,
            SettingDurationUnit.Hours => span.TotalHours,
            _ => span.TotalDays,
        };

        return ((long)Math.Round(amount)).ToString(CultureInfo.InvariantCulture);
    }
}
