using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The API contract gate (AD-37, rule R13). The committed snapshot at
/// <c>contracts/openapi-v1.json</c> is the published shape of v1; this test fails whenever
/// the generated document differs from it.
///
/// It intentionally fails on *any* difference, additive ones included. A tool that could
/// decide by itself which changes are breaking would have to encode the compatibility rules,
/// and it would be wrong at the edges — whereas a diff in a pull request puts the decision in
/// front of a person who can apply the rules from
/// ARCHITECTURE-PLATFORM-CAPABILITIES.md §6.1: additive is fine within a version, anything
/// else needs a new one. The snapshot is the forcing function; the review is the gate.
///
/// To accept an intended change, regenerate and read the diff:
/// <code>
/// UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/GymOS.IntegrationTests
/// git diff contracts/openapi-v1.json
/// </code>
/// </summary>
[Collection(SqlServerCollectionDefinition.Name)]
public sealed class OpenApiContractTests : IAsyncLifetime
{
    private const string SnapshotEnvironmentVariable = "UPDATE_OPENAPI_SNAPSHOT";

    private readonly SqlServerFixture _sqlServer;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public OpenApiContractTests(SqlServerFixture sqlServer) => _sqlServer = sqlServer;

    public async Task InitializeAsync()
    {
        string connectionString = await _sqlServer.CreateDatabaseAsync("openapi_contract_tests");
        _host = AuthenticationHost.Create(connectionString);
        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task TheGeneratedDocumentMatchesTheCommittedSnapshot()
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        string generated = Normalize(await response.Content.ReadAsStringAsync());
        FileInfo snapshot = SnapshotFile();

        if (Environment.GetEnvironmentVariable(SnapshotEnvironmentVariable) == "1")
        {
            snapshot.Directory!.Create();
            await File.WriteAllTextAsync(snapshot.FullName, generated, Encoding.UTF8);
            return;
        }

        Assert.True(
            snapshot.Exists,
            $"No contract snapshot at {snapshot.FullName}. Create it with: " +
            $"{SnapshotEnvironmentVariable}=1 dotnet test tests/GymOS.IntegrationTests");

        string committed = Normalize(await File.ReadAllTextAsync(snapshot.FullName));

        Assert.True(
            string.Equals(committed, generated, StringComparison.Ordinal),
            "The v1 OpenAPI document no longer matches contracts/openapi-v1.json.\n\n" +
            "If the change is additive (new endpoint, new optional field) it is compatible " +
            "within v1: regenerate the snapshot and include it in the same commit.\n" +
            "If it removes, renames, retypes, or changes the meaning of anything, it is " +
            "breaking and needs a new API version instead (AD-37 §6.1).\n\n" +
            $"  {SnapshotEnvironmentVariable}=1 dotnet test tests/GymOS.IntegrationTests\n" +
            "  git diff contracts/openapi-v1.json");
    }

    /// <summary>
    /// Reformats with sorted keys so the comparison reflects the contract rather than the
    /// generator's property ordering, which is not part of the contract and would otherwise
    /// produce diffs nobody caused.
    /// </summary>
    private static string Normalize(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            WriteSorted(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan).ReplaceLineEndings("\n").TrimEnd() + "\n";
    }

    private static void WriteSorted(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(property.Value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteSorted(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static FileInfo SnapshotFile() =>
        new(Path.Combine(RepositoryRoot().FullName, "contracts", "openapi-v1.json"));

    private static DirectoryInfo RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GymOS.Backend.slnx")))
        {
            directory = directory.Parent;
        }

        return directory
            ?? throw new InvalidOperationException("Could not locate the backend root (GymOS.Backend.slnx).");
    }
}
