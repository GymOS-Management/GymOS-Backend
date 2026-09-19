using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.BuildingBlocks.Application.DependencyInjection;
using GymOS.BuildingBlocks.Application.Messaging;
using GymOS.Platform.Application.DependencyInjection;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Application.Settings.Abstractions;
using GymOS.Platform.Application.UnitTests.Fakes;
using GymOS.Platform.Domain.Identity;
using GymOS.TestingSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GymOS.Platform.Application.UnitTests;

/// <summary>
/// Composes the use cases exactly as the host does — real dispatcher, real handler
/// registration from <see cref="PlatformApplicationServiceCollectionExtensions"/>, fakes
/// only at the seams. Resolving through the container rather than newing handlers up
/// means a handler that exists but was never registered fails these tests instead of
/// failing in production.
/// </summary>
internal sealed class IdentityTestContext
{
    public static readonly DateTimeOffset Start = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _provider;

    public IdentityTestContext(Action<LoginPolicyOptions>? configurePolicy = null)
    {
        var policy = new LoginPolicyOptions();
        configurePolicy?.Invoke(policy);
        Policy = policy;

        UserQueries = new FakeUserQueries(Users);

        var services = new ServiceCollection();

        services.AddCqrsDispatcher();

        // The real authorization behavior, in the real pipeline position: permission
        // enforcement is then exercised by every handler test rather than only by the tests
        // written for it.
        services.AddAuthorizationBehavior();
        services.AddPlatformApplication();

        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<IUserRepository>(Users);
        services.AddSingleton<IRefreshTokenRepository>(RefreshTokens);
        services.AddSingleton<IRoleRepository>(Roles);
        services.AddSingleton<IIdentityQueries>(IdentityQueries);
        services.AddSingleton<IUserQueries>(UserQueries);

        // The last-SuperAdmin guard serializes on a set-level lock (see SuperAdminGuard).
        services.AddSingleton<IAdvisoryLock>(AdvisoryLock);
        services.AddSingleton<IPasswordHasher>(PasswordHasher);
        services.AddSingleton<IAccessTokenIssuer>(AccessTokens);
        services.AddSingleton<IRefreshTokenGenerator>(RefreshTokenGenerator);
        services.AddSingleton<IUserActivationTokenRepository>(ActivationTokens);
        services.AddSingleton<IActivationTokenGenerator>(ActivationTokenGenerator);
        services.AddSingleton<IActivationLinks>(ActivationLinks);
        services.AddSingleton<IUnitOfWork>(UnitOfWork);
        services.AddSingleton<ICurrentUser>(CurrentUser);
        services.AddSingleton<IPermissionEvaluator>(Permissions);
        services.AddSingleton<IAuditTrail>(AuditTrail);
        services.AddSingleton<ISettings<LoginPolicyOptions>>(new FakeSettings<LoginPolicyOptions>(policy));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        _provider = services.BuildServiceProvider();
    }

    public FakeClock Clock { get; } = new(Start);

    public FakeUserRepository Users { get; } = new();

    public FakeRefreshTokenRepository RefreshTokens { get; } = new();

    public FakeIdentityQueries IdentityQueries { get; } = new();

    public FakePasswordHasher PasswordHasher { get; } = new();

    public FakeAccessTokenIssuer AccessTokens { get; } = new();

    public FakeRefreshTokenGenerator RefreshTokenGenerator { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    /// <summary>Records the set-level locks the SuperAdmin guard takes.</summary>
    public FakeAdvisoryLock AdvisoryLock { get; } = new();

    public FakeRoleRepository Roles { get; } = new();

    public FakeUserQueries UserQueries { get; }

    public FakeCurrentUser CurrentUser { get; } = new();

    public FakePermissionEvaluator Permissions { get; } = new();

    public FakeAuditTrail AuditTrail { get; } = new();

    public FakeActivationTokenRepository ActivationTokens { get; } = new();

    public FakeActivationTokenGenerator ActivationTokenGenerator { get; } = new();

    public FakeActivationLinks ActivationLinks { get; } = new();

    public LoginPolicyOptions Policy { get; }

    public IDispatcher Dispatcher => _provider.GetRequiredService<IDispatcher>();

    /// <summary>An active user with a known password, ready to sign in.</summary>
    public User GivenActiveUser(string email = "admin@gymos.test", string password = "correct-horse-battery")
    {
        var user = User.Create(email, "Test", "Admin");
        user.SetPasswordHash(PasswordHasher.Hash(password), Clock.UtcNow);
        Users.Seed(user);

        IdentityQueries.Profiles[user.Id] = new UserProfile(
            user.Id, user.Email, user.FirstName, user.LastName, user.Status.ToString(), ["SuperAdmin"]);

        return user;
    }

    /// <summary>
    /// Acts as an administrator holding <paramref name="permissions"/>. Without this, the
    /// authorization behavior refuses every permission-bearing request — which is the point,
    /// but it means a test about something else has to say who it is first.
    /// </summary>
    public IdentityTestContext ActingAs(Guid userId, params string[] permissions)
    {
        CurrentUser.UserId = userId;
        CurrentUser.Email = "actor@gymos.test";

        foreach (string permission in permissions)
        {
            Permissions.Granted.Add(permission);
        }

        return this;
    }

    /// <summary>A seeded platform role, registered so role-resolving commands can find it.</summary>
    public Role GivenPlatformRole(string name)
    {
        Role role = Role.Create(name, $"{name} role", RoleScope.Platform, isSystem: true);
        Roles.Seed(role);
        return role;
    }

    /// <summary>Records a refresh token as though it had been issued by a sign-in.</summary>
    public RefreshToken GivenRefreshToken(Guid userId, string rawToken, TimeSpan? lifetime = null)
    {
        RefreshToken token = RefreshToken.Issue(
            userId,
            RefreshTokenGenerator.ComputeHash(rawToken),
            Clock.UtcNow.Add(lifetime ?? Policy.RefreshTokenLifetime),
            "203.0.113.7");

        RefreshTokens.Seed(token);
        return token;
    }
}
