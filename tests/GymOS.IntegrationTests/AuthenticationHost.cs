using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GymOS.IntegrationTests;

/// <summary>
/// The real host, configured entirely through <c>UseSetting</c> — no dependency on
/// appsettings files or the machine's environment, so these tests assert on the code
/// rather than on a developer's local configuration.
/// </summary>
internal sealed class AuthenticationHost : IAsyncDisposable
{
    /// <summary>Long enough to satisfy the boot-time signing-key check; a test value, not a secret.</summary>
    public const string SigningKey = "integration-test-signing-key-well-over-32-bytes-long";

    public const string SuperAdminEmail = "superadmin@gymos.test";
    public const string SuperAdminPassword = "integration-test-password";

    private readonly WebApplicationFactory<Program> _factory;

    private AuthenticationHost(WebApplicationFactory<Program> factory, string connectionString)
    {
        _factory = factory;
        ConnectionString = connectionString;
    }

    /// <summary>Exposed so a test can boot a second host against the same database.</summary>
    public string ConnectionString { get; }

    public static AuthenticationHost Create(
        string connectionString,
        Action<Dictionary<string, string>>? overrideSettings = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:Database"] = connectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Security:Jwt:SigningKey"] = SigningKey,
            ["Seed:SuperAdmin:Email"] = SuperAdminEmail,
            ["Seed:SuperAdmin:Password"] = SuperAdminPassword,

            // Argon2 at its floor: these tests exercise the flow, not the cost parameters
            // (those have their own unit tests), and production cost per sign-in would
            // dominate the suite's runtime.
            ["Security:PasswordHashing:MemorySizeKib"] = "8192",
            ["Security:PasswordHashing:Iterations"] = "1",
        };

        overrideSettings?.Invoke(settings);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach ((string key, string value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

        return new AuthenticationHost(factory, connectionString);
    }

    /// <summary>
    /// A client that does <b>not</b> manage cookies. Automatic handling actively gets in
    /// the way here: these tests need to present a *specific* refresh token — including
    /// one that has already been rotated away — and a cookie container would quietly
    /// attach its own current value alongside it, so the server would see two cookies
    /// and the test would pass or fail on which one it happened to read.
    ///
    /// The https base address stays because the cookie is marked <c>Secure</c>; keeping
    /// the scheme honest means the attribute is exercised rather than worked around.
    /// </summary>
    public HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        HandleCookies = false,
    });

    public IServiceProvider Services => _factory.Services;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();
}

/// <summary>Auth endpoint calls, kept out of the tests so assertions stay readable.</summary>
internal static class AuthApi
{
    public static readonly Uri Login = new("/api/v1/auth/login", UriKind.Relative);
    public static readonly Uri Refresh = new("/api/v1/auth/refresh", UriKind.Relative);
    public static readonly Uri Logout = new("/api/v1/auth/logout", UriKind.Relative);
    public static readonly Uri Me = new("/api/v1/auth/me", UriKind.Relative);
    public static readonly Uri ChangePassword = new("/api/v1/auth/change-password", UriKind.Relative);

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync(Login, new { email, password });

    public static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client,
        string currentPassword,
        string newPassword) =>
        client.PostAsJsonAsync(ChangePassword, new { currentPassword, newPassword });

    public static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>The RFC 7807 <c>code</c> extension member — the stable part clients branch on.</summary>
    public static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out JsonElement code) ? code.GetString() : null;
    }

    public static void Authorize(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}
