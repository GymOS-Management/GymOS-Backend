using GymOS.Platform.Domain.Settings;

namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>Reads and change-tracks <c>platform.platform_settings</c> rows (M7 spec §3). Every row a
/// key has ever been given lives here; the catalog itself lives in <see cref="PlatformSettingDefinitions"/>.</summary>
public interface ISettingsRepository
{
    Task<IReadOnlyList<PlatformSetting>> ListAllAsync(CancellationToken ct);

    Task<PlatformSetting?> FindByKeyAsync(string key, CancellationToken ct);

    void Add(PlatformSetting setting);

    void Remove(PlatformSetting setting);
}
