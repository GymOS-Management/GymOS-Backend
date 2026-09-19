using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Domain.Settings;
using GymOS.Platform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// Resolution order and secrecy through the real host on real SQL Server (M7 spec §3–4): no row →
/// configuration → default; a sensitive value round-trips through Data Protection with the key ring in
/// the database; a fresh scope decrypts what another encrypted.
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class SettingsProviderTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;

    public SettingsProviderTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("settings_provider");
        _host = AuthenticationHost.Create(connectionString, settings =>
        {
            settings["Jobs:Enabled"] = "false";
            settings["Email:Smtp:Host"] = "configured.example.test"; // the configuration layer
        });
        using HttpClient client = _host.CreateClient();
        using HttpResponseMessage ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _sqlServer.DropDatabaseAsync(_host.ConnectionString);
    }

    private static EffectiveSetting Find(IReadOnlyList<EffectiveSetting> all, string key) =>
        all.Single(setting => setting.Definition.Key == key);

    [Fact]
    public async Task ResolutionIsRowThenConfigurationThenDefault()
    {
        using IServiceScope scope = _host.Services.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<ISettingsProvider>();

        IReadOnlyList<EffectiveSetting> all = await provider.GetEffectiveAsync(CancellationToken.None);
        Assert.Equal(("configured.example.test", SettingSource.Configuration), (Find(all, "email.smtp_host").Value, Find(all, "email.smtp_host").Source));
        Assert.Equal(("1025", SettingSource.Default), (Find(all, "email.smtp_port").Value, Find(all, "email.smtp_port").Source));
        Assert.Equal(("15", SettingSource.Default), (Find(all, "security.lockout_minutes").Value, Find(all, "security.lockout_minutes").Source));

        await SeedRowAsync("email.smtp_host", plain: "row.example.test");
        provider.Invalidate();
        all = await provider.GetEffectiveAsync(CancellationToken.None);

        Assert.Equal(("row.example.test", SettingSource.Database), (Find(all, "email.smtp_host").Value, Find(all, "email.smtp_host").Source));
        IReadOnlyDictionary<string, string?> overlay = await provider.GetOverlayAsync(CancellationToken.None);
        Assert.Equal("row.example.test", overlay["Email:Smtp:Host"]);
        Assert.False(overlay.ContainsKey("Email:Smtp:Port")); // only database rows overlay
    }

    [Fact]
    public async Task ASensitiveValueIsStoredAsCiphertextAndReadBackInTheClear()
    {
        using (IServiceScope scope = _host.Services.CreateScope())
        {
            var protector = scope.ServiceProvider.GetRequiredService<ISettingProtector>();
            string ciphertext = protector.Protect("email.smtp_password", "s3cret");
            Assert.NotEqual("s3cret", ciphertext);
            await SeedRowAsync("email.smtp_password", protectedValue: ciphertext);
        }

        await using (AppDbContext context = _host.CreateDbContext())
        {
            PlatformSetting row = await context.Set<PlatformSetting>().AsNoTracking().SingleAsync(s => s.Key == "email.smtp_password");
            Assert.Null(row.Value);
            Assert.DoesNotContain("s3cret", row.ProtectedValue, StringComparison.Ordinal);
            Assert.True(await context.DataProtectionKeys.AnyAsync()); // the ring is in the database
        }

        using IServiceScope second = _host.Services.CreateScope();
        var provider = second.ServiceProvider.GetRequiredService<ISettingsProvider>();
        provider.Invalidate();
        IReadOnlyList<EffectiveSetting> all = await provider.GetEffectiveAsync(CancellationToken.None);
        Assert.Equal("s3cret", Find(all, "email.smtp_password").Value);
    }

    private async Task SeedRowAsync(string key, string? plain = null, string? protectedValue = null)
    {
        Assert.True(PlatformSettingDefinitions.TryGet(key, out SettingDefinition definition));
        await using AppDbContext context = _host.CreateDbContext();
        context.Set<PlatformSetting>().Add(PlatformSetting.Create(Guid.NewGuid(), definition, plain, protectedValue, null));
        await context.SaveChangesAsync();
    }
}
