namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>A strongly typed slice of the effective settings — the shape a consumer such as the rate
/// limiter or the maintenance middleware binds against, refreshed as <see cref="ISettingsProvider"/>'s
/// cache reloads rather than read once at startup.</summary>
public interface ISettings<T>
    where T : class, new()
{
    T Current { get; }
}
