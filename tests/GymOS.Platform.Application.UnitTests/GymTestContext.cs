using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Gyms.Abstractions;
using GymOS.Platform.Application.Subscriptions.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Gyms;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Composes the gym use cases exactly as the host does — real dispatcher, real handler registration,
/// fakes only at the seams. Resolving through the container means a handler that exists but was never
/// registered fails these tests instead of failing in production.
/// </summary>
internal sealed class GymTestContext
{
    public static readonly DateTimeOffset Start = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public GymTestContext()
    {
        GymQueries = new FakeGymQueries(Gyms, Permissions);

        var services = new ServiceCollection();

        services.AddCqrsDispatcher();

        // The real authorization behavior, in the real pipeline position: permission enforcement is
        // then exercised by every handler test rather than only by the tests written for it.
        services.AddAuthorizationBehavior();
        services.AddPlatformApplication();

        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<IGymRepository>(Gyms);
        services.AddSingleton<IGymQueries>(GymQueries);

        // Deleting a gym cancels its live subscription in the same transaction, so the lifecycle
        // handlers reach the subscription write side even in gym-only tests.
        services.AddSingleton<ISubscriptionRepository>(Subscriptions);
        services.AddSingleton<IUnitOfWork>(UnitOfWork);
        services.AddSingleton<ICurrentUser>(CurrentUser);
        services.AddSingleton<IPermissionEvaluator>(Permissions);
        services.AddSingleton<IAuditTrail>(AuditTrail);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        _provider = services.BuildServiceProvider();
    }

    public FakeClock Clock { get; } = new(Start);

    public FakeGymRepository Gyms { get; } = new();

    /// <summary>Deleting a gym must also end what it was paying for — see the delete handler.</summary>
    public FakeSubscriptionRepository Subscriptions { get; } = new();

    public FakeGymQueries GymQueries { get; }

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public FakeCurrentUser CurrentUser { get; } = new();

    public FakePermissionEvaluator Permissions { get; } = new();

    public FakeAuditTrail AuditTrail { get; } = new();

    public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

    /// <summary>
    /// Acts as an administrator holding <paramref name="permissions"/>. Without this the authorization
    /// behavior refuses every permission-bearing request — which is the point, but it means a test about
    /// something else has to say who it is first.
    /// </summary>
    public GymTestContext ActingAs(params string[] permissions)
    {
        CurrentUser.UserId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        CurrentUser.Email = "operator@gymos.test";

        foreach (string permission in permissions)
        {
            Permissions.Granted.Add(permission);
        }

        return this;
    }

    /// <summary>A gym in <see cref="GymStatus.Provisioning"/>, with its empty branding record.</summary>
    public Gym GivenProvisioningGym(string slug = "acme-gym")
    {
        Gym gym = Gym.Provision(
            slug,
            "Acme Gym",
            new GymOwnerContact("Ada Owner", "ada@acme.test", null),
            new GymLocale("CY", "Asia/Nicosia", "EUR"));

        Gyms.Seed(gym);
        Gyms.SeedBranding(GymBranding.CreateEmpty(gym.Id));

        return gym;
    }

    public Gym GivenActiveGym(string slug = "acme-gym")
    {
        Gym gym = GivenProvisioningGym(slug);
        gym.Activate();
        return gym;
    }

    public Gym GivenSuspendedGym(string slug = "acme-gym", string reason = "Non-payment")
    {
        Gym gym = GivenActiveGym(slug);
        gym.Suspend(reason);
        return gym;
    }

    public Gym GivenDeletedGym(string slug = "acme-gym")
    {
        Gym gym = GivenActiveGym(slug);
        gym.Delete(Clock.UtcNow);
        return gym;
    }
}
