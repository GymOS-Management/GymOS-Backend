using System.Diagnostics.CodeAnalysis;

namespace GymOS.Platform.Domain.Settings;

/// <summary>The data type a setting can hold.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The frozen schema (ARCHITECTURE.md §2.7) names the value types String, Int, Bool and Json; the members are those names.")]
public enum SettingValueType { String = 1, Int = 2, Bool = 3, Json = 4 }
