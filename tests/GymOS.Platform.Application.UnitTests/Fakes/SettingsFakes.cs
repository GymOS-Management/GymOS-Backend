using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.SharedKernel.Results;

namespace GymOS.Platform.Application.UnitTests.Fakes;

internal sealed class FakeSettingsRepository : ISettingsRepository
{
    public List<PlatformSetting> Rows { get; } = [];

    public Task<IReadOnlyList<PlatformSetting>> ListAllAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PlatformSetting>>(Rows.ToList());

    public Task<PlatformSetting?> FindByKeyAsync(string key, CancellationToken ct) =>
        Task.FromResult(Rows.SingleOrDefault(r => r.Key == key));

    public void Add(PlatformSetting setting) => Rows.Add(setting);

    public void Remove(PlatformSetting setting) => Rows.Remove(setting);
}

/// <summary>Resolves from the fake repository the way the real provider does, minus the cache — so a test sees its own writes.</summary>
internal sealed class FakeSettingsProvider : ISettingsProvider
{
    private readonly FakeSettingsRepository _rows;
    private readonly ISettingProtector _protector;

    public FakeSettingsProvider(FakeSettingsRepository rows, ISettingProtector protector)
    {
        _rows = rows;
        _protector = protector;
    }

    public int Invalidations { get; private set; }

    public Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EffectiveSetting>>(PlatformSettingDefinitions.All.Select(d =>
        {
            PlatformSetting? row = _rows.Rows.SingleOrDefault(r => r.Key == d.Key);
            return row is null
                ? new EffectiveSetting(d, d.Default, SettingSource.Default, null, null)
                : new EffectiveSetting(d, d.IsSensitive ? _protector.Unprotect(d.Key, row.ProtectedValue!) : row.Value, SettingSource.Database, row.UpdatedBy, row.UpdatedAtUtc);
        }).ToList());

    public async Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct) =>
        (await GetEffectiveAsync(ct)).Where(e => e.Source == SettingSource.Database && e.Definition.ConfigurationPath is not null)
            .ToDictionary(e => e.Definition.ConfigurationPath!, e => e.Definition.ToConfigurationValue(e.Value!));

    public IReadOnlyDictionary<string, string?> GetOverlayNow() =>
        GetOverlayAsync(CancellationToken.None).GetAwaiter().GetResult();

    public void Invalidate() => Invalidations++;
}

internal sealed class FakeSettingProtector : ISettingProtector
{
    public string Protect(string key, string plaintext) => $"enc({key}:{plaintext})";

    public string Unprotect(string key, string ciphertext) => ciphertext[(5 + key.Length)..^1];
}

internal sealed class FakeSettingsValidator : ISettingsValidator
{
    public string? RejectWith { get; set; }

    public IReadOnlyDictionary<string, string?>? LastOverlay { get; private set; }

    public Task<Result> ValidateAsync(SettingCategory category, IReadOnlyDictionary<string, string?> overlay, CancellationToken ct)
    {
        LastOverlay = overlay;
        return Task.FromResult(RejectWith is null ? Result.Success() : Result.Failure(SettingErrors.InvalidState(RejectWith)));
    }
}
