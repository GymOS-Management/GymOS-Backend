using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests;

public sealed class DispatcherTests
{
    private sealed record Ping(string Message) : ICommand<string>;

    private sealed record Lookup(int Number) : IQuery<int>;

    private sealed record Orphan : ICommand<string>;

    private sealed class PingHandler : ICommandHandler<Ping, string>
    {
        public Task<string> HandleAsync(Ping command, CancellationToken cancellationToken) =>
            Task.FromResult("pong:" + command.Message);
    }

    private sealed class LookupHandler : IQueryHandler<Lookup, int>
    {
        public Task<int> HandleAsync(Lookup query, CancellationToken cancellationToken) =>
            Task.FromResult(query.Number * 2);
    }

    private sealed class CallLog
    {
        public List<string> Entries { get; } = [];
    }

    private sealed class OuterBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly CallLog _log;

        public OuterBehavior(CallLog log) => _log = log;

        public async Task<TResponse> HandleAsync(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
        {
            _log.Entries.Add("outer:before");
            TResponse response = await next();
            _log.Entries.Add("outer:after");
            return response;
        }
    }

    private sealed class InnerBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
        where TRequest : notnull
    {
        private readonly CallLog _log;

        public InnerBehavior(CallLog log) => _log = log;

        public async Task<TResponse> HandleAsync(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken)
        {
            _log.Entries.Add("inner:before");
            TResponse response = await next();
            _log.Entries.Add("inner:after");
            return response;
        }
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCqrsDispatcher();
        services.AddHandlersFromAssembly(typeof(DispatcherTests).Assembly);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SendAsync_RoutesCommandToItsHandler()
    {
        await using ServiceProvider provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        string response = await dispatcher.SendAsync(new Ping("hello"));

        Assert.Equal("pong:hello", response);
    }

    [Fact]
    public async Task QueryAsync_RoutesQueryToItsHandler()
    {
        await using ServiceProvider provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        int response = await dispatcher.QueryAsync(new Lookup(21));

        Assert.Equal(42, response);
    }

    [Fact]
    public async Task SendAsync_WithoutARegisteredHandler_ThrowsWithGuidance()
    {
        await using ServiceProvider provider = BuildProvider();
        var services = new ServiceCollection();
        services.AddCqrsDispatcher();
        await using ServiceProvider bare = services.BuildServiceProvider();
        var dispatcher = bare.GetRequiredService<IDispatcher>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Orphan()));

        Assert.Contains(nameof(Orphan), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Behaviors_ComposeInRegistrationOrder_FirstRegisteredOutermost()
    {
        var log = new CallLog();
        await using ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton(log);
            services.AddPipelineBehavior(typeof(OuterBehavior<,>));
            services.AddPipelineBehavior(typeof(InnerBehavior<,>));
        });
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Ping("x"));

        Assert.Equal(
            ["outer:before", "inner:before", "inner:after", "outer:after"],
            log.Entries);
    }

    [Fact]
    public void AddPipelineBehavior_RejectsTypesThatAreNotBehaviors()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddPipelineBehavior(typeof(CallLog)));
    }

    [Fact]
    public async Task RepeatedDispatch_UsesTheCachedInvoker()
    {
        await using ServiceProvider provider = BuildProvider();
        var dispatcher = provider.GetRequiredService<IDispatcher>();

        Assert.Equal("pong:a", await dispatcher.SendAsync(new Ping("a")));
        Assert.Equal("pong:b", await dispatcher.SendAsync(new Ping("b")));
    }
}
