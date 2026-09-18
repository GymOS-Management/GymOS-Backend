using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The document and its Scalar reference UI are published together, and only when the host is
/// configured to publish them.
///
/// <para><b>Why this is tested rather than assumed.</b> The document is a complete map of every
/// endpoint, parameter and schema. Before this gate existed it was served unauthenticated in
/// every environment, production included — nothing failed, nothing logged, and the exposure was
/// invisible from inside the application. A default that is only correct until someone edits
/// <c>Program.cs</c> is not a default; these tests are what make it one.</para>
///
/// <para><b>Isolation.</b> Each case boots its own host, so the suite gets its own database and
/// leaves the shared one alone (the standing rule in STATUS.md). Jobs are off and migrations are
/// skipped: these tests route HTTP and touch no data, so running the renewal and overdue sweeps
/// against a database other suites are asserting on would be pure interference — which is exactly
/// what an earlier version of this file caused.</para>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class OpenApiExposureTests : IAsyncLifetime
{
    private readonly SqlServerFixture _sqlServer;
    private string _connectionString = null!;

    public OpenApiExposureTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync() =>
        _connectionString = await _sqlServer.CreateIsolatedDatabaseAsync("openapi_exposure");

    public Task DisposeAsync() => _sqlServer.DropDatabaseAsync(_connectionString);

    [Fact]
    public async Task ScalarReferenceUi_IsServed_WhenExposed()
    {
        await using WebApplicationFactory<Program> factory = CreateHost(exposeOpenApi: null);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/scalar/v1", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// A UI that loads but cannot fetch its document is worse than no UI, so the two are gated by
    /// one condition and asserted as a pair.
    /// </summary>
    [Theory]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    public async Task Nothing_IsServed_WhenTheGateIsClosed(string path)
    {
        await using WebApplicationFactory<Program> factory = CreateHost(exposeOpenApi: false);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The setting overrides the environment in both directions — an environment that is not
    /// Development can still publish the reference deliberately.
    /// </summary>
    [Fact]
    public async Task ExplicitlyEnabled_PublishesOutsideDevelopment()
    {
        await using WebApplicationFactory<Program> factory = CreateHost(
            exposeOpenApi: true,
            environment: "Staging");
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The default with no setting present: on in Development, off everywhere else. This is the
    /// behaviour an operator gets by doing nothing, which is the one worth pinning.
    /// </summary>
    /// <remarks>
    /// Staging is deliberately not a case here. It ships an <c>OpenApi:Enabled</c> of its own in
    /// appsettings.Staging.json, so "no setting present" is not a state a Staging host can be in, and
    /// asserting the code default through it would be asserting that file instead. Production still
    /// covers "off outside Development", which is the half that matters; the file's own choice is
    /// pinned by <see cref="StagingShipsASettingOfItsOwn_AndPublishes"/>.
    /// </remarks>
    [Theory]
    [InlineData("Development", System.Net.HttpStatusCode.OK)]
    [InlineData("Production", System.Net.HttpStatusCode.NotFound)]
    public async Task WithNoSetting_TheEnvironmentDecides(string environment, System.Net.HttpStatusCode expected)
    {
        await using WebApplicationFactory<Program> factory = CreateHost(
            exposeOpenApi: null,
            environment: environment);
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>
    /// UAT publishes the document, because appsettings.Staging.json says so and means it: the base
    /// appsettings names a staging or internal environment as exactly where publishing is reasonable,
    /// and QA and the console developers read the contract there rather than standing up a stack.
    /// </summary>
    /// <remarks>
    /// A separate test rather than a row in the theory above, because it pins a different thing: not
    /// the framework default for a non-Development environment, but a committed file's deliberate
    /// override of it. If appsettings.Staging.json ever stops enabling the document, this is what
    /// should fail, and it should fail by name rather than as "the default moved".
    /// </remarks>
    [Fact]
    public async Task StagingShipsASettingOfItsOwn_AndPublishes()
    {
        await using WebApplicationFactory<Program> factory = CreateHost(
            exposeOpenApi: null,
            environment: "Staging");
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    private WebApplicationFactory<Program> CreateHost(bool? exposeOpenApi, string? environment = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Database", _connectionString);

            // No schema and no seeding: nothing here reads data, and both seeders skip with a
            // warning while migrations are pending. Keeps seven host boots cheap.
            builder.UseSetting("Database:MigrateOnStartup", "false");

            // Off deliberately — see the isolation note on the class.
            builder.UseSetting("Jobs:Enabled", "false");

            // Configured explicitly rather than inherited from appsettings, so these assertions
            // hold regardless of a developer's local files (the AuthenticationHost convention).
            builder.UseSetting("Security:Jwt:SigningKey", AuthenticationHost.SigningKey);

            if (environment is not null)
            {
                builder.UseEnvironment(environment);
            }

            if (exposeOpenApi is not null)
            {
                builder.UseSetting("OpenApi:Enabled", exposeOpenApi.Value ? "true" : "false");
            }
        });
}
