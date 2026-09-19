using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Identity;
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

        services.AddBoundSettings<EmailOptions>(EmailOptions.SectionName);
        services.AddBoundSettings<LoginPolicyOptions>(LoginPolicyOptions.SectionName);
        services.AddBoundSettings<JwtOptions>(JwtOptions.SectionName);
        services.AddBoundSettings<MonitoringOptions>(MonitoringOptions.SectionName);
        services.AddBoundSettings<MaintenanceOptions>(MaintenanceOptions.SectionName);
        services.AddBoundSettings<RateLimitOptions>(RateLimitOptions.SectionName);

        services.TryAddScoped<ISettingsValidator, OptionsSettingsValidator>();

        return services;
    }

    public static IServiceCollection AddBoundSettings<T>(this IServiceCollection services, string sectionName) where T : class, new()
    {
        services.TryAddSingleton(new SettingsBinding<T>(sectionName));

        // Singleton, not scoped: the last-valid fallback SettingsBinder<T> keeps across an invalid
        // overlay must survive past the request that hit it, or every fresh scope after would rebind
        // from nothing and throw. Its dependencies are all singleton-safe (see SettingsBinder<T>'s doc).
        services.TryAddSingleton<ISettings<T>, SettingsBinder<T>>();
        return services;
    }
}
