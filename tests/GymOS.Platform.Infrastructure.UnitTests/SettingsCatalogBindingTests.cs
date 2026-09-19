using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Settings;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>Every catalog path lands on a real options property, and durations overlay as the TimeSpan their property expects.</summary>
public sealed class SettingsCatalogBindingTests
{
    [Fact]
    public void EveryConfigurationPathBindsToAKnownOptionsProperty()
    {
        Dictionary<string, string?> overlay = PlatformSettingDefinitions.All
            .Where(d => d.ConfigurationPath is not null)
            .ToDictionary(d => d.ConfigurationPath!, d => d.ToConfigurationValue(d.Default ?? (d.ValueType == SettingValueType.Int ? "1" : "x")));
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(overlay).Build();

        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var login = configuration.GetSection(LoginPolicyOptions.SectionName).Get<LoginPolicyOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var jwt = configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var rate = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var maintenance = configuration.GetSection(MaintenanceOptions.SectionName).Get<MaintenanceOptions>(o => o.ErrorOnUnknownConfiguration = true)!;
        var monitoring = configuration.GetSection(MonitoringOptions.SectionName).Get<MonitoringOptions>(o => o.ErrorOnUnknownConfiguration = true)!;

        Assert.Equal(1025, email.Smtp.Port);
        Assert.Equal(TimeSpan.FromMinutes(15), login.LockoutDuration);
        Assert.Equal(TimeSpan.FromDays(7), login.RefreshTokenLifetime);
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.AccessTokenLifetime);
        Assert.Equal(60, rate.SignInWindowSeconds);
        Assert.False(maintenance.Enabled);
        Assert.Equal(30, monitoring.JobRunRetentionDays);
    }
}
