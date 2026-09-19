using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GymOS.IntegrationTests;

/// <summary>
/// Just enough SMTP to drive Task 8's connection tester (<c>MailKitSmtpConnectionTester</c>) without a
/// real mail server: a greeting, <c>EHLO</c>/<c>HELO</c> advertising PLAIN and LOGIN, <c>AUTH</c>
/// accepted or refused per <see cref="RejectAuth"/>, <c>NOOP</c>, and <c>QUIT</c>. No clock — a fake
/// this small has nothing to timestamp.
/// </summary>
internal sealed class FakeSmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeSmtpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public int Port { get; }

    /// <summary>When true, every <c>AUTH</c> attempt gets <c>535 bad credentials</c> instead of <c>235 ok</c>.</summary>
    public bool RejectAuth { get; set; }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Best-effort shutdown: the accept loop's own catches already cover the expected
            // cancellation/disposal paths, and a test teardown must not fail on what it triggered.
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken);
                await HandleAsync(client, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // The listener was stopped from DisposeAsync — expected, not a test failure.
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        await writer.WriteLineAsync("220 fake ESMTP");

        string authResult = RejectAuth ? "535 bad credentials" : "235 ok";
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("HELO", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250-fake");
                await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
            }
            else if (line.StartsWith("AUTH LOGIN", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("334 VXNlcm5hbWU6"); // "Username:"
                await reader.ReadLineAsync(cancellationToken);
                await writer.WriteLineAsync("334 UGFzc3dvcmQ6"); // "Password:"
                await reader.ReadLineAsync(cancellationToken);
                await writer.WriteLineAsync(authResult);
            }
            else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(line.Trim(), "AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                {
                    // The initial-response form ("AUTH PLAIN <base64>") is one line; this bare form
                    // needs a 334 continuation before the credentials arrive on their own line.
                    await writer.WriteLineAsync("334 ");
                    await reader.ReadLineAsync(cancellationToken);
                }

                await writer.WriteLineAsync(authResult);
            }
            else if (line.StartsWith("NOOP", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("250 ok");
            }
            else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                await writer.WriteLineAsync("221 bye");
                break;
            }
            else
            {
                await writer.WriteLineAsync("250 ok");
            }
        }
    }
}
