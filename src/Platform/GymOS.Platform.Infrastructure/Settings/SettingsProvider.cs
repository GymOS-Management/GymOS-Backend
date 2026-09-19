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
///
/// <para>Stale-while-revalidate for synchronous readers (<see cref="GetOverlayNow"/>): a write's
/// <see cref="Invalidate"/> marks the snapshot stale rather than dropping it, so a synchronous caller
/// such as <see cref="SettingsBinder{T}"/> always has something to return immediately and only ever
/// blocks on the very first load of a cold process. The async readers
/// (<see cref="GetEffectiveAsync"/>, <see cref="GetOverlayAsync"/>) still await a fresh load whenever
/// the snapshot is stale or past its TTL, which is what lets the write path's own re-read see its
/// just-written row.</para>
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
    private volatile bool _stale;
    private int _refreshInFlight;

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

    public IReadOnlyDictionary<string, string?> GetOverlayNow()
    {
        Snapshot? snapshot = _snapshot;
        if (snapshot is null)
        {
            // Cold process: there is no stale snapshot to serve while a refresh happens in the
            // background, so this one call pays the same blocking cost every other reader already pays
            // on the very first request after start-up.
            return GetSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult().Overlay;
        }

        if (!IsFresh(snapshot))
        {
            TriggerBackgroundRefresh();
        }

        return snapshot.Overlay;
    }

    public void Invalidate() => _stale = true;

    /// <summary>Releases the reload semaphore. The provider is a singleton, so this runs once, at
    /// host shutdown.</summary>
    public void Dispose() => _reloadLock.Dispose();

    /// <summary>Starts exactly one background reload if none is already in flight; fire-and-forget from
    /// the synchronous <see cref="GetOverlayNow"/> path, so a burst of stale reads triggers a single
    /// refresh rather than one per caller.</summary>
    private void TriggerBackgroundRefresh()
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // GetSnapshotAsync already falls back to the previous snapshot and logs on a reload
                // failure; this only catches something going wrong around that, such as the semaphore
                // having been disposed during shutdown.
                LogReloadFailed(_logger, exception);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }

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
                _stale = false;
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
        snapshot is not null && !_stale && snapshot.LoadedAt + _ttl >= _clock.UtcNow;

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
                if (definition.IsSensitive && row.ProtectedValue is null)
                {
                    // The check constraint on platform_settings (ck_platform_settings_one_value) only
                    // forbids both value and protected_value being null, or neither — it does not tie
                    // the populated column to the catalog's IsSensitive flag. A row for a sensitive
                    // definition with `value` set and `protected_value` null satisfies the constraint
                    // and reaches here, so this guard is load-bearing, not defensive: without it, the
                    // Unprotect call below throws ArgumentNullException and takes the whole reload down.
                    LogUnprotectFailed(_logger, definition.Key, new InvalidOperationException(
                        $"Sensitive setting '{definition.Key}' has a null protected value."));
                    decrypted = false;
                }
                else
                {
                    try
                    {
                        value = definition.IsSensitive ? protector.Unprotect(definition.Key, row.ProtectedValue!) : row.Value;
                    }
                    catch (CryptographicException exception)
                    {
                        LogUnprotectFailed(_logger, definition.Key, exception);
                        decrypted = false;
                    }
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
