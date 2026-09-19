using GymOS.WebApi.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// A server process that opens a browser must be impossible to get by accident. These pin the
/// registration decision rather than the launch itself: if the service is never registered, there
/// is nothing that can misfire, and the decision is the part a future edit could quietly widen.
///
/// <para>The launcher currently also no-ops under <c>WebApplicationFactory</c>, because
/// <c>TestServer</c> binds no address for it to open — verified, but a property of the test host
/// rather than of this code. These tests are what keep CI tab-free if that ever changes.</para>
/// </summary>
public sealed class ApiReferenceBrowserRegistrationTests
{
    [Fact]
    public void Development_WithTheReferencePublished_RegistersTheLauncher() =>
        Assert.True(IsRegistered(Environments.Development));

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void OutsideDevelopment_NothingIsRegistered(string environment) =>
        Assert.False(IsRegistered(environment));

    /// <summary>
    /// A UI that is not served must not be opened — the tab would land on a 404.
    /// </summary>
    [Fact]
    public void WhenTheReferenceIsNotPublished_NothingIsRegistered() =>
        Assert.False(IsRegistered(
            Environments.Development,
            settings: new Dictionary<string, string?> { ["OpenApi:Enabled"] = "false" }));

    [Fact]
    public void TheDeveloperCanTurnItOff() =>
        Assert.False(IsRegistered(
            Environments.Development,
            settings: new Dictionary<string, string?> { ["OpenApi:LaunchBrowser"] = "false" }));

    /// <summary>
    /// The override works in both directions, including inside a container: the three guards decide
    /// the default, and an explicit setting replaces the default.
    /// </summary>
    [Fact]
    public void AnExplicitTrue_OverridesTheEnvironmentDefault() =>
        Assert.True(IsRegistered(
            Environments.Production,
            settings: new Dictionary<string, string?> { ["OpenApi:LaunchBrowser"] = "true" }));

    /// <summary>
    /// The one deployment where opening a browser is not merely useless but confusing. Set by the
    /// official .NET base images; read through a seam so this test does not depend on where CI runs.
    /// </summary>
    [Fact]
    public void InsideAContainer_NothingIsRegisteredByDefault() =>
        Assert.False(IsRegistered(Environments.Development, inContainer: true));

    [Fact]
    public void InsideAContainer_AnExplicitTrueStillWins() =>
        Assert.True(IsRegistered(
            Environments.Development,
            settings: new Dictionary<string, string?> { ["OpenApi:LaunchBrowser"] = "true" },
            inContainer: true));

    private static bool IsRegistered(string environment, Dictionary<string, string?>? settings = null, bool inContainer = false)
    {
        var services = new ServiceCollection();

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        services.AddGymOSApiReferenceBrowser(
            configuration,
            new StubEnvironment(environment),
            readEnvironmentVariable: name =>
                string.Equals(name, "DOTNET_RUNNING_IN_CONTAINER", StringComparison.Ordinal) && inContainer ? "true" : null);

        return services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService));
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public StubEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "GymOS.WebApi";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
