using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace GymOS.Platform.Infrastructure.UnitTests;

/// <summary>
/// <see cref="SettingsBinder{T}"/> in isolation from the real <see cref="SettingsProvider"/> and from
/// SQL Server: a fake provider hands back whatever overlay a test sets, and the real
/// <see cref="EmailOptions"/> validators (the same <c>.Validate</c> calls production registers) decide
/// whether it binds. Covers the fallback order the binder promises: last valid value, else the layer
/// below the overlay, and never an exception while either exists.
/// </summary>
public sealed class SettingsBinderTests
{
    private sealed class FakeSettingsProvider : ISettingsProvider
    {
        public IReadOnlyDictionary<string, string?> Overlay { get; set; } = new Dictionary<string, string?>();

        public Task<IReadOnlyList<EffectiveSetting>> GetEffectiveAsync(CancellationToken ct) =>
            throw new NotSupportedException("Not exercised by SettingsBinder<T>.");

        public Task<IReadOnlyDictionary<string, string?>> GetOverlayAsync(CancellationToken ct) => Task.FromResult(Overlay);

        public IReadOnlyDictionary<string, string?> GetOverlayNow() => Overlay;

        public void Invalidate() { }
    }

    /// <summary>The same three cross-field rules <c>PlatformIdentityServiceCollectionExtensions</c>
    /// registers for <see cref="EmailOptions"/> in production, built the same way (<c>AddOptions&lt;T&gt;().Validate</c>)
    /// so this test exercises the real validation pipeline, not a stand-in for it.</summary>
    private static IEnumerable<IValidateOptions<EmailOptions>> RealEmailValidators()
    {
        var services = new ServiceCollection();
        services.AddOptions<EmailOptions>()
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Smtp.Host),
                "Smtp:Host is required when Enabled is true.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.From.Address),
                "From:Address is required when Enabled is true.")
            .Validate(
                options => !options.Enabled || Uri.TryCreate(options.ConsoleBaseUrl, UriKind.Absolute, out _),
                "ConsoleBaseUrl must be an absolute URL when Enabled is true.");

        return services.BuildServiceProvider().GetServices<IValidateOptions<EmailOptions>>();
    }

    private static readonly Dictionary<string, string?> ValidOverlay = new()
    {
        ["Email:Enabled"] = "true",
        ["Email:Smtp:Host"] = "smtp.example.test",
        ["Email:From:Address"] = "noreply@example.test",
        ["Email:From:DisplayName"] = "GymOS",
        ["Email:ConsoleBaseUrl"] = "https://console.example.test",
    };

    private static (ISettings<EmailOptions> Settings, FakeSettingsProvider Provider) CreateBinder(
        Dictionary<string, string?>? configuration = null)
    {
        var provider = new FakeSettingsProvider();
        IConfigurationRoot config = new ConfigurationBuilder()
            .AddInMemoryCollection(configuration ?? [])
            .Build();

        var binder = new SettingsBinder<EmailOptions>(
            provider, config, new SettingsBinding<EmailOptions>(EmailOptions.SectionName),
            RealEmailValidators(), NullLogger<SettingsBinder<EmailOptions>>.Instance);

        return (binder, provider);
    }

    [Fact]
    public void AValidOverlayBinds()
    {
        (ISettings<EmailOptions> settings, FakeSettingsProvider provider) = CreateBinder();
        provider.Overlay = ValidOverlay;

        EmailOptions current = settings.Current;

        Assert.True(current.Enabled);
        Assert.Equal("smtp.example.test", current.Smtp.Host);
        Assert.Equal("noreply@example.test", current.From.Address);
    }

    [Fact]
    public void AnInvalidOverlayWithNoPriorBindFallsBackToConfiguration()
    {
        // The configuration layer alone (Enabled=false) is valid and is what ValidateOnStart already
        // proved at boot; the overlay enables email without an SMTP host, which fails EmailOptions' own
        // validator, and there is no previous valid bind yet to serve instead.
        (ISettings<EmailOptions> settings, FakeSettingsProvider provider) = CreateBinder(
            new Dictionary<string, string?> { ["Email:Enabled"] = "false" });
        provider.Overlay = new Dictionary<string, string?> { ["Email:Enabled"] = "true" };

        EmailOptions current = settings.Current;

        Assert.False(current.Enabled);
    }

    [Fact]
    public void AnInvalidOverlayAfterAValidOneServesThePreviousValidOne()
    {
        (ISettings<EmailOptions> settings, FakeSettingsProvider provider) = CreateBinder();
        provider.Overlay = ValidOverlay;

        EmailOptions valid = settings.Current;
        Assert.True(valid.Enabled);

        // A fresh overlay reference, as a real reload would hand the binder, that fails validation.
        provider.Overlay = new Dictionary<string, string?>(ValidOverlay) { ["Email:ConsoleBaseUrl"] = "not a url" };

        EmailOptions current = settings.Current;

        Assert.Same(valid, current); // the previous valid bind, not a re-bind of the broken overlay
    }
}
