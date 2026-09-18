using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.BuildingBlocks.UnitTests.Jobs;

public sealed class RegisteredJobsCatalogTests
{
    private sealed class AlphaJob : IScheduledJob
    {
        public string Name => "alpha";
        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMinutes(15));
        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class BrokenJob : IScheduledJob
    {
        public BrokenJob() => throw new InvalidOperationException("no dependencies for me");
        public string Name => "broken";
        public JobSchedule Schedule => JobSchedule.Every(TimeSpan.FromMinutes(1));
        public Task ExecuteAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void ItListsEveryRegisteredJobsNameAndSchedule()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<AlphaJob>();
        services.AddSingleton<IRegisteredJobs>(provider => new RegisteredJobsCatalog(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegisteredJobsCatalog>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();

        RegisteredJob job = Assert.Single(provider.GetRequiredService<IRegisteredJobs>().All);

        Assert.Equal("alpha", job.Name);
        Assert.Equal(TimeSpan.FromMinutes(15), job.Schedule.Period);
    }

    [Fact]
    public void AJobThatCannotBeConstructedIsOmittedNotFatal()
    {
        var services = new ServiceCollection();
        services.AddScheduledJob<AlphaJob>();
        services.AddScheduledJob<BrokenJob>();
        services.AddSingleton<IRegisteredJobs>(provider => new RegisteredJobsCatalog(
            provider.GetServices<ScheduledJobRegistration>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RegisteredJobsCatalog>.Instance));
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(["alpha"], provider.GetRequiredService<IRegisteredJobs>().All.Select(job => job.Name));
    }
}
