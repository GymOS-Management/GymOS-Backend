namespace GymOS.Platform.Application.Settings.Abstractions;

/// <summary>The resolved view of every declared setting (M7 spec §3): a per-instance cache, TTL
/// <c>Settings:CacheTtlSeconds</c>, invalidated after every write; other replicas simply wait out the
/// TTL rather than being told to reload.</summary>
public interface ISettingsProvider
{
    /// <summary>Every definition, decrypted, resolved row-then-configuration-then-default, from cache.</summary>
    Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct);

    /// <summary>Database-sourced rows only, keyed by <see cref="Domain.Settings.SettingDefinition.ConfigurationPath"/>,
    /// for layering onto <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct);

    /// <summary>The last loaded overlay, possibly one refresh stale; triggers a background refresh when
    /// stale or missing; blocks only when no snapshot has ever been loaded.</summary>
    IReadOnlyDictionary<string, string?> GetOverlayNow();

    /// <summary>Marks the cached snapshot stale so the next read reloads from the database — the
    /// snapshot itself is kept so a synchronous reader (<see cref="GetOverlayNow"/>) still has
    /// something to serve while that reload happens.</summary>
    void Invalidate();
}
