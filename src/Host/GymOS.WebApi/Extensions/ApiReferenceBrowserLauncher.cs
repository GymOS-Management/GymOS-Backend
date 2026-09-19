using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// Opens the Scalar API reference in a browser once the host is listening, so starting the API
/// locally lands on its documentation.
///
/// <para><b>Why this is in the application and not in <c>launchSettings.json</c>.</b> That file's
/// <c>launchBrowser</c> is honoured by Visual Studio, Rider and <c>dotnet watch</c> — but
/// <b>not by <c>dotnet run</c></b>, which is how this host is usually started and how the
/// README documents starting it. Verified rather than assumed: with <c>launchBrowser: true</c>,
/// <c>dotnet run</c> opened nothing. Doing it here covers every launcher with one mechanism,
/// which is also why <c>launchBrowser</c> stays <c>false</c> — both would open two tabs.</para>
///
/// <para><b>Why it cannot fire where it would be wrong.</b> A server process opening a browser is
/// correct on a developer machine and absurd anywhere else, so this is registered only when the
/// reference is actually published (<see cref="OpenApiExtensions.IsOpenApiExposed"/>), the
/// environment is Development, and the process is not in a container — a containerised host has no
/// display, and its "browser" would be a stray process inside the container. <c>OpenApi:LaunchBrowser</c>
/// overrides the decision either way, for the developer who finds an opening tab unwelcome.</para>
///
/// <para>The address comes from the server rather than from configuration, so it is right when a
/// port is overridden or the framework picks one.</para>
/// </summary>
internal sealed partial class ApiReferenceBrowserLauncher : IHostedService
{
    private readonly IServer _server;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ApiReferenceBrowserLauncher> _logger;

    public ApiReferenceBrowserLauncher(
        IServer server,
        IHostApplicationLifetime lifetime,
        ILogger<ApiReferenceBrowserLauncher> logger)
    {
        _server = server;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Addresses are only bound once the server has started; ApplicationStarted is the
        // first point at which asking for one gives an answer.
        _lifetime.ApplicationStarted.Register(Launch);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Launch()
    {
        string? address = _server.Features
            .Get<IServerAddressesFeature>()?.Addresses
            .FirstOrDefault();

        if (address is null)
        {
            LogNoAddress(_logger);
            return;
        }

        // The server reports http://[::]:8080 / http://0.0.0.0:8080 for a wildcard binding,
        // neither of which a browser can open.
        string url = $"{address.Replace("[::]", "localhost", StringComparison.Ordinal)
            .Replace("0.0.0.0", "localhost", StringComparison.Ordinal)
            .TrimEnd('/')}/scalar/v1";

        try
        {
            // UseShellExecute hands the URL to the OS default handler — the only form that
            // works without hard-coding a browser per platform.
            using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            if (browser is null)
            {
                // With UseShellExecute, null means the OS handed the URL to a handler that was already
                // running — the usual case on Windows when a browser is open — or that nothing accepted
                // it. Neither is "opened"; say what is actually known.
                LogHandedToExistingHandler(_logger, url);
                return;
            }

            LogOpened(_logger, url);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A machine with no registered browser handler is a normal state, not a failure of
            // the API. Never take the host down for a convenience.
            LogFailed(_logger, url, exception);
        }
    }

    [LoggerMessage(EventId = 6100, Level = LogLevel.Information, Message = "API reference opened at {Url}")]
    private static partial void LogOpened(ILogger logger, string url);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Debug, Message = "No server address available; API reference not opened")]
    private static partial void LogNoAddress(ILogger logger);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning, Message = "Could not open the API reference at {Url}")]
    private static partial void LogFailed(ILogger logger, string url, Exception exception);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Information, Message = "API reference at {Url} was handed to the default handler; no new browser process was started")]
    private static partial void LogHandedToExistingHandler(ILogger logger, string url);
}
