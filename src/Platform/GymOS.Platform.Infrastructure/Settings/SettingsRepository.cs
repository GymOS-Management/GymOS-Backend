using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GymOS.Platform.Infrastructure.Settings;

/// <summary>EF Core over <c>platform.platform_settings</c> (M7 spec §3). Rows come back tracked —
/// unlike the read-only query classes elsewhere — because the write handlers that use this repository
/// load a row and mutate it in place with <see cref="PlatformSetting.Set"/>.</summary>
internal sealed class SettingsRepository : ISettingsRepository
{
    private readonly AppDbContext _dbContext;

    public SettingsRepository(AppDbContext dbContext) => _dbContext = dbContext;

    public Task<IReadOnlyList<PlatformSetting>> ListAllAsync(CancellationToken ct) =>
        ListAllCoreAsync(ct);

    private async Task<IReadOnlyList<PlatformSetting>> ListAllCoreAsync(CancellationToken ct) =>
        await _dbContext.Set<PlatformSetting>().ToListAsync(ct);

    public Task<PlatformSetting?> FindByKeyAsync(string key, CancellationToken ct) =>
        _dbContext.Set<PlatformSetting>().SingleOrDefaultAsync(setting => setting.Key == key, ct);

    public void Add(PlatformSetting setting) => _dbContext.Set<PlatformSetting>().Add(setting);

    public void Remove(PlatformSetting setting) => _dbContext.Set<PlatformSetting>().Remove(setting);
}
