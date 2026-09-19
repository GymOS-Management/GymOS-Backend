using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Jobs;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.BuildingBlocks.Application.Security;
using GymOS.Platform.Application.Analytics.Abstractions;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Monitoring;
using GymOS.Platform.Application.Monitoring.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Domain.Monitoring;
using GymOS.SharedKernel.Results;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GymOS.Platform.Application.UnitTests;

public sealed class MonitoringQueryHandlerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeMonitoringQueries : IMonitoringQueries
    {
        public HashSet<string> JobsWithRuns { get; } = [];

        public JobRunFilter? LastFilter { get; private set; }

        public int RunsToReturn { get; set; }

        public Task<IReadOnlyDictionary<string, JobHistoryHead>> GetJobHistoryHeadsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, JobHistoryHead>>(new Dictionary<string, JobHistoryHead>());

        public Task<bool> HasRunsAsync(string jobName, CancellationToken cancellationToken) =>
            Task.FromResult(JobsWithRuns.Contains(jobName));

        public Task<IReadOnlyList<JobRunDto>> ListRunsAsync(JobRunFilter filter, CancellationToken cancellationToken)
        {
            LastFilter = filter;
            IReadOnlyList<JobRunDto> runs = Enumerable.Range(0, RunsToReturn)
                .Select(i => new JobRunDto(Guid.NewGuid(), filter.JobName, "h", "Succeeded", Start.AddMinutes(-i), null, null, null))
                .ToList();
            return Task.FromResult(runs);
        }

        public Task<OutboxDepth> GetOutboxDepthAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(new OutboxDepth(0, 0, null, OutboxDepth.PendingProcessorNote));
    }

    private sealed class FakeRegisteredJobs : IRegisteredJobs
    {
        public IReadOnlyList<RegisteredJob> All { get; } =
        [
            new("renewal", JobSchedule.Every(TimeSpan.FromMinutes(15))),
        ];
    }

    private sealed class FakeDashboardHealthCheck : IDashboardHealthCheck
    {
        public Task<DashboardHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DashboardHealth("Healthy", []));
    }

    private sealed class FakeProcessMetrics : IProcessMetrics
    {
        public ProcessMetrics? Current => null;
    }

    private sealed class Fixture
    {
        private readonly ServiceProvider _provider;

        public Fixture()
        {
            var services = new ServiceCollection();

            services.AddCqrsDispatcher();
            services.AddAuthorizationBehavior();
            services.AddPlatformApplication();

            services.AddSingleton<IMonitoringQueries>(Queries);
            services.AddSingleton<IRegisteredJobs>(new FakeRegisteredJobs());
            services.AddSingleton<IClock>(Clock);
            services.AddSingleton<ISettings<MonitoringOptions>>(new FakeSettings<MonitoringOptions>(new MonitoringOptions()));
            services.AddSingleton<IDashboardHealthCheck>(new FakeDashboardHealthCheck());
            services.AddSingleton<IProcessMetrics>(new FakeProcessMetrics());

            services.AddSingleton<ICurrentUser>(CurrentUser);
            services.AddSingleton<IPermissionEvaluator>(Permissions);
            services.AddSingleton<IAuditTrail>(AuditTrail);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

            _provider = services.BuildServiceProvider();
        }

        public FakeMonitoringQueries Queries { get; } = new();

        public FakeClock Clock { get; } = new(Start);

        public FakeCurrentUser CurrentUser { get; } = new();

        public FakePermissionEvaluator Permissions { get; } = new();

        public FakeAuditTrail AuditTrail { get; } = new();

        public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

        public Fixture ActingAs(params string[] permissions)
        {
            CurrentUser.UserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            CurrentUser.Email = "operator@gymos.test";

            foreach (string permission in permissions)
            {
                Permissions.Granted.Add(permission);
            }

            return this;
        }
    }

    [Fact]
    public async Task AnUnknownJobIsNotFound()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("nope", null, null, null, 50), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("monitoring.job_not_found", result.Error!.Code);
    }

    [Fact]
    public async Task ARenamedJobWithHistoryIsStillReadable()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);
        fixture.Queries.JobsWithRuns.Add("old-name");

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("old-name", null, null, null, 50), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AnInvalidStatusIsAValidationError()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("renewal", null, null, "Exploded", 50), CancellationToken.None);

        Assert.Equal("monitoring.invalid_status", result.Error!.Code);
    }

    [Fact]
    public async Task FromAfterToIsAValidationError()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);
        DateTimeOffset now = fixture.Clock.UtcNow;

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("renewal", now, now.AddHours(-1), null, 50), CancellationToken.None);

        Assert.Equal("monitoring.invalid_range", result.Error!.Code);
    }

    [Fact]
    public async Task TheLimitIsClampedAndHasMoreFlipsAtTheBoundary()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);
        fixture.Queries.RunsToReturn = 201;

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("renewal", null, null, "Succeeded", 5000), CancellationToken.None);

        Assert.Equal(201, fixture.Queries.LastFilter!.Take); // limit 200, plus one to detect more
        Assert.Equal(JobRunStatus.Succeeded, fixture.Queries.LastFilter.Status);
        Assert.Equal(200, result.Value.Runs.Count);
        Assert.True(result.Value.HasMore);
    }

    [Fact]
    public async Task FewerRunsThanTheLimitMeansThereIsNoMore()
    {
        Fixture fixture = new Fixture().ActingAs(PlatformPermissions.MonitoringRead);
        fixture.Queries.RunsToReturn = 200; // exactly the clamped limit, not one more

        Result<JobRunsPage> result = await fixture.Dispatcher.QueryAsync(
            new ListJobRunsQuery("renewal", null, null, "Succeeded", 5000), CancellationToken.None);

        Assert.Equal(200, result.Value.Runs.Count);
        Assert.False(result.Value.HasMore);
    }

    [Fact]
    public async Task ACallerWithoutMonitoringReadIsRefused()
    {
        Fixture fixture = new Fixture().ActingAs(); // authenticated, but holds nothing

        PermissionDeniedException denied = await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            fixture.Dispatcher.QueryAsync(
                new ListJobRunsQuery("renewal", null, null, null, 50), CancellationToken.None));

        Assert.Equal(PlatformPermissions.MonitoringRead, denied.Permission);
        Assert.Equal(nameof(ListJobRunsQuery), denied.RequestName);
    }
}
