using System.Security.Cryptography;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>
/// The resolved view of every declared setting (M7 spec §3): a per-process singleton cache, TTL
/// <see cref="SettingsProviderOptions.CacheTtlSeconds"/>, invalidated after every write by the write
/// handlers; other replicas simply wait out the TTL rather than being told to reload. A definition
/// resolves row, then configuration, then its own default — see <see cref="LoadAsync"/>.
/// </summary>
internal sealed partial class SettingsProvider : ISettingsProvider, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IClock _clock;
    private readonly TimeSpan _ttl;
    private readonly ILogger<SettingsProvider> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    private volatile Snapshot? _snapshot;

    public SettingsProvider(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IClock clock,
        IOptions<SettingsProviderOptions> options,
        ILogger<SettingsProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _clock = clock;
        _ttl = TimeSpan.FromSeconds(options.Value.CacheTtlSeconds);
        _logger = logger;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Settings row '{Key}' has no matching definition in the catalog and was skipped.")]
    private static partial void LogOrphanRow(ILogger logger, string key);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Failed to decrypt setting '{Key}'; treating it as if it had no stored value.")]
    private static partial void LogUnprotectFailed(ILogger logger, string key, Exception exception);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Settings reload failed; continuing to serve the previous cached snapshot.")]
    private static partial void LogReloadFailed(ILogger logger, Exception exception);

    public async Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.Effective;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct)
    {
        Snapshot snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.Overlay;
    }

    public void Invalidate() => _snapshot = null;

    /// <summary>Releases the reload semaphore. The provider is a singleton, so this runs once, at
    /// host shutdown.</summary>
    public void Dispose() => _reloadLock.Dispose();

    private async Task<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        Snapshot? snapshot = _snapshot;
        if (IsFresh(snapshot))
        {
            return snapshot!;
        }

        await _reloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            snapshot = _snapshot;
            if (IsFresh(snapshot))
            {
                return snapshot!;
            }

            try
            {
                Snapshot loaded = await LoadAsync(ct).ConfigureAwait(false);
                _snapshot = loaded;
                return loaded;
            }
            catch (Exception exception)
            {
                if (_snapshot is { } previous)
                {
                    LogReloadFailed(_logger, exception);
                    return previous;
                }

                throw;
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private bool IsFresh(Snapshot? snapshot) =>
        snapshot is not null && snapshot.LoadedAt + _ttl >= _clock.UtcNow;

    private async Task<Snapshot> LoadAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ISettingProtector>();

        IReadOnlyList<PlatformSetting> rows = await repository.ListAllAsync(ct).ConfigureAwait(false);

        var rowsByKey = new Dictionary<string, PlatformSetting>(StringComparer.Ordinal);
        foreach (PlatformSetting row in rows)
        {
            if (!PlatformSettingDefinitions.TryGet(row.Key, out _))
            {
                LogOrphanRow(_logger, row.Key);
                continue;
            }

            rowsByKey[row.Key] = row;
        }

        var effective = new List<EffectiveSetting>(PlatformSettingDefinitions.All.Count);
        var overlay = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (SettingDefinition definition in PlatformSettingDefinitions.All)
        {
            if (rowsByKey.TryGetValue(definition.Key, out PlatformSetting? row))
            {
                string? value = null;
                bool decrypted = true;
                try
                {
                    value = definition.IsSensitive ? protector.Unprotect(definition.Key, row.ProtectedValue!) : row.Value;
                }
                catch (CryptographicException exception)
                {
                    LogUnprotectFailed(_logger, definition.Key, exception);
                    decrypted = false;
                }

                if (decrypted)
                {
                    if (definition.ConfigurationPath is not null)
                    {
                        overlay[definition.ConfigurationPath] = definition.ToConfigurationValue(value!);
                    }

                    effective.Add(new EffectiveSetting(definition, value, SettingSource.Database, row.UpdatedBy, row.UpdatedAtUtc));
                    continue;
                }
            }

            if (definition.ConfigurationPath is not null && _configuration[definition.ConfigurationPath] is { Length: > 0 } raw)
            {
                effective.Add(new EffectiveSetting(definition, definition.FromConfigurationValue(raw), SettingSource.Configuration, null, null));
                continue;
            }

            effective.Add(new EffectiveSetting(definition, definition.Default, SettingSource.Default, null, null));
        }

        return new Snapshot(_clock.UtcNow, effective, overlay);
    }

    private sealed record Snapshot(DateTimeOffset LoadedAt, IReadOnlyList<EffectiveSetting> Effective, IReadOnlyDictionary<string, string?> Overlay);
}
