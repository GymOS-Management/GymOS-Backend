using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using GymOS.BuildingBlocks.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The activation flow over real HTTP and real PostgreSQL (ADR-53).
///
/// <para>The journey here is the one that proves the hole is closed: invite an administrator, follow
/// the link, set a password, and <b>sign in with it</b>. Before this increment that last step was
/// impossible for any account except the seeded SuperAdmin.</para>
///
/// <para>Email is <em>enabled</em> here with its transport replaced by a capture, so the real link
/// building and the real message bodies are exercised without an SMTP server. Reading the token out
/// of the sent message is not a shortcut — it is the only way to obtain one, because the database
/// holds nothing but a SHA-256 and the API deliberately never returns it (asserted below).</para>
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ActivationApiTests : IAsyncLifetime
{
    private static readonly Uri Users = new("/api/v1/users", UriKind.Relative);
    private static readonly Uri SetPassword = new("/api/v1/auth/set-password", UriKind.Relative);

    private const string ConsoleBaseUrl = "https://console.gymos.test";

    private readonly PostgresFixture _postgres;
    private AuthenticationHost _host = null!;
    private HttpClient _client = null!;

    public ActivationApiTests(PostgresFixture postgres) => _postgres = postgres;

    private readonly CapturingEmailSender _mailbox = new();

    public async Task InitializeAsync()
    {
        string connectionString = await _postgres.CreateIsolatedDatabaseAsync("activation");

        // Email on, with the transport swapped for a capture: that exercises the real
        // ActivationLinks and the real templates — the URL, the lifetime wording, both bodies — and
        // is the only way to obtain the raw token, which by design exists nowhere else.
        _host = AuthenticationHost.Create(
            connectionString,
            settings =>
            {
                settings["Email:Enabled"] = "true";
                settings["Email:Smtp:Host"] = "unused-because-the-sender-is-replaced";
                settings["Email:From:Address"] = "platform@gymos.test";
                settings["Email:From:DisplayName"] = "GymOS";
                settings["Email:ConsoleBaseUrl"] = ConsoleBaseUrl;
            },
            services => services.AddSingleton<IEmailSender>(_mailbox));

        _client = _host.CreateClient();

        using HttpResponseMessage ready = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        using HttpResponseMessage signIn = await AuthApi.LoginAsync(
            _client, AuthenticationHost.SuperAdminEmail, AuthenticationHost.SuperAdminPassword);
        AuthApi.Authorize(_client, await AuthApi.ReadAccessTokenAsync(signIn));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task AnInvitedAdministrator_SetsAPasswordAndSignsIn()
    {
        const string email = "activated@gymos.test";
        const string password = "a-perfectly-fine-password";

        Guid userId = await InviteAsync(email);

        // Never returned by the API — the only copy is in the link that was emailed.
        string token = ReadTokenFromLatestEmail(email);

        using HttpResponseMessage check = await _client.GetAsync(
            new Uri($"{SetPassword}?token={Uri.EscapeDataString(token)}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);

        using HttpResponseMessage set = await AnonymousPostAsync(token, password);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        // The point of the whole increment.
        using HttpClient anonymous = _host.CreateClient();
        using HttpResponseMessage signIn = await anonymous.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative), new { email, password });

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);

        // And the account is Active rather than still PendingActivation.
        using HttpResponseMessage detail = await _client.GetAsync(
            new Uri($"/api/v1/users/{userId}", UriKind.Relative));
        JsonElement body = await detail.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Active", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheLinkWorksOnce_AndEveryFailureLooksTheSame()
    {
        await InviteAsync("once@gymos.test");
        string token = ReadTokenFromLatestEmail("once@gymos.test");

        using HttpResponseMessage first = await AnonymousPostAsync(token, "a-perfectly-fine-password");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using HttpResponseMessage replayed = await AnonymousPostAsync(token, "another-fine-password");
        using HttpResponseMessage unknown = await AnonymousPostAsync("not-a-real-token", "another-fine-password");

        // Byte-identical, deliberately — and 404 for all four causes rather than a 410 that would
        // itself say "this existed once". A distinct "expired" or "already used" would confirm to an
        // anonymous caller that a token had once been real. This is the assertion most likely to rot
        // as somebody adds a more helpful message.
        Assert.Equal(HttpStatusCode.NotFound, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Everything except `traceId`, which is per-request by design and the one field that is
        // *supposed* to differ. Comparing whole bodies would fail on it and prove nothing.
        Assert.Equal(await DescribeProblemAsync(replayed), await DescribeProblemAsync(unknown));
    }

    [Fact]
    public async Task ReSendingAnInvitation_KillsTheLinkItReplaces()
    {
        Guid userId = await InviteAsync("resent@gymos.test");
        string first = ReadTokenFromLatestEmail("resent@gymos.test");

        using HttpResponseMessage resend = await _client.PostAsync(
            new Uri($"{Users}/{userId}/resend-invitation", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.NoContent, resend.StatusCode);

        // The header the console reads to distinguish "invitation sent" from "created, but the mail
        // did not go". Email is disabled here, which counts as sent — nothing failed.
        Assert.Equal("sent", Assert.Single(resend.Headers.GetValues("X-Invitation-Email")));

        using HttpResponseMessage withOldLink = await AnonymousPostAsync(first, "a-perfectly-fine-password");
        Assert.Equal(HttpStatusCode.NotFound, withOldLink.StatusCode);

        string second = ReadTokenFromLatestEmail("resent@gymos.test");
        Assert.NotEqual(first, second);

        using HttpResponseMessage withNewLink = await AnonymousPostAsync(second, "a-perfectly-fine-password");
        Assert.Equal(HttpStatusCode.NoContent, withNewLink.StatusCode);
    }

    [Fact]
    public async Task ResendingToAnActivatedAccount_IsRefused()
    {
        Guid userId = await InviteAsync("already@gymos.test");
        await AnonymousPostAsync(ReadTokenFromLatestEmail("already@gymos.test"), "a-perfectly-fine-password");

        using HttpResponseMessage response = await _client.PostAsync(
            new Uri($"{Users}/{userId}/resend-invitation", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("users.not_pending_activation", problem.GetProperty("code").GetString());
    }

    /// <summary>The problem body without its per-request <c>traceId</c>.</summary>
    private static async Task<string> DescribeProblemAsync(HttpResponseMessage response)
    {
        JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        return string.Join(
            '|',
            problem.EnumerateObject()
                .Where(member => member.Name != "traceId")
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .Select(member => $"{member.Name}={member.Value}"));
    }

    /// <summary>Posts without the SuperAdmin's token — the endpoint has to work for a stranger.</summary>
    private async Task<HttpResponseMessage> AnonymousPostAsync(string token, string password)
    {
        using HttpClient anonymous = _host.CreateClient();

        return await anonymous.PostAsJsonAsync(SetPassword, new { token, newPassword = password });
    }

    private async Task<Guid> InviteAsync(string email)
    {
        using HttpResponseMessage created = await _client.PostAsJsonAsync(Users, new
        {
            email,
            firstName = "Test",
            lastName = "Invitee",
            roleIds = Array.Empty<Guid>(),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The invitation's delivery outcome rides beside the body rather than in it, so the response
        // shape is unchanged from before ADR-53.
        Assert.Equal("sent", Assert.Single(created.Headers.GetValues("X-Invitation-Email")));

        string body = await created.Content.ReadAsStringAsync();

        // The response must not carry the token: it is a credential, and the only place it belongs
        // is the email.
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);

        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Pulls the token out of the link in the most recent message.
    ///
    /// <para>The database holds only a SHA-256, so this is the only way to obtain it — which is the
    /// property being relied on rather than a limitation of the test.</para>
    /// </summary>
    private string ReadTokenFromLatestEmail(string expectedRecipient)
    {
        EmailMessage message = _mailbox.Messages.Last(sent => sent.To == expectedRecipient);

        // Asserted on the plain-text body: a security email that only works in an HTML client is one
        // this increment deliberately does not send.
        Match match = Regex.Match(message.TextBody, @"token=([A-Za-z0-9_-]+)");

        Assert.True(match.Success, $"no activation link in the message body: {message.TextBody}");
        Assert.Contains(ConsoleBaseUrl, message.TextBody, StringComparison.Ordinal);

        return Uri.UnescapeDataString(match.Groups[1].Value);
    }
}

/// <summary>Holds what would have been sent, so a test can read the link out of it.</summary>
internal sealed class CapturingEmailSender : IEmailSender
{
    public List<EmailMessage> Messages { get; } = [];

    public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);

        return Task.FromResult(EmailSendResult.Success);
    }
}
