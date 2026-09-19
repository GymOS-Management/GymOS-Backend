using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes M7 settings: the declared catalog's repository and protector, the cached
/// <see cref="ISettingsProvider"/>, the two options classes it feeds, and Data Protection with its key
/// ring in <see cref="AppDbContext"/>. Called after <c>AddPlatformPersistence</c>, which registers
/// <see cref="AppDbContext"/> itself.
/// </summary>
public static class PlatformSettingsServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformSettings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SettingsProviderOptions>().Bind(configuration.GetSection(SettingsProviderOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
        services.AddOptions<MaintenanceOptions>().Bind(configuration.GetSection(MaintenanceOptions.SectionName)).ValidateOnStart();
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();

        // The key ring lives in the database so a replica scaled from zero still decrypts (M7 spec §3).
        // Key Vault protection of the ring is wired in Program.cs, where the Azure packages live.
        services.AddDataProtection().SetApplicationName("GymOS").PersistKeysToDbContext<AppDbContext>();

        services.TryAddScoped<ISettingsRepository, SettingsRepository>();
        services.TryAddScoped<ISettingProtector, DataProtectionSettingProtector>();
        services.TryAddSingleton<ISettingsProvider, SettingsProvider>();

        return services;
    }
}
