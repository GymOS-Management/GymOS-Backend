using GymOS.WebApi.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GymOS.IntegrationTests;

/// <summary>
/// The canceled-request contract: a caller who hung up gets a quiet 499 and no error, while a
/// cancellation on a request whose caller is still waiting stays a real failure. Tested directly
/// rather than through a live server — an abort must land mid-request to reproduce over HTTP,
/// which is a race, and races make flaky tests.
/// </summary>
public sealed class CanceledRequestHandlingTests
{
    private static readonly ExceptionHandlingExtensions.CanceledRequestExceptionHandler Handler = new();

    [Fact]
    public async Task ACanceledQueryOnAnAbortedRequest_IsHandledAsClientClosedRequest()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };

        bool handled = await Handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(
            ExceptionHandlingExtensions.CanceledRequestExceptionHandler.ClientClosedRequest,
            httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task ACancellationWhileTheCallerStillWaits_IsNotSwallowed()
    {
        // The request is alive: this OperationCanceledException is a genuine defect (a stray
        // token, an internal timeout) and must fall through to the 500 it deserves.
        var httpContext = new DefaultHttpContext();

        bool handled = await Handler.TryHandleAsync(
            httpContext, new OperationCanceledException(), CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task OtherExceptionsOnAnAbortedRequest_AreNotSwallowed()
    {
        // A real failure that happened to occur on an aborted request is still a real failure —
        // the abort must not become a blanket excuse.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };

        bool handled = await Handler.TryHandleAsync(
            httpContext, new InvalidOperationException(), CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task TaskCancellation_CountsAsCancellation()
    {
        // TaskCanceledException derives from OperationCanceledException; async database drivers
        // throw either depending on where the token lands.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var httpContext = new DefaultHttpContext { RequestAborted = aborted.Token };

        bool handled = await Handler.TryHandleAsync(
            httpContext, new TaskCanceledException(), CancellationToken.None);

        Assert.True(handled);
    }
}
