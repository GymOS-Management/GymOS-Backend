using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GymOS.Platform.Infrastructure.Persistence.Seeding;

/// <summary>
/// Creates the first SuperAdmin so a fresh installation is reachable — and nothing more.
///
/// Two rules make this safe to run on every startup of every instance:
/// it only ever *creates*, never resets an existing account's password (otherwise a
/// stale environment variable would silently overwrite a real administrator's
/// credential), and it does nothing at all unless both the email and the password are
/// configured, logging a warning that says exactly how to configure them.
/// </summary>
public sealed partial class SuperAdminSeeder
{
    private readonly AppDbContext _dbContext;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IClock _clock;
    private readonly IOptions<SuperAdminSeedOptions> _seedOptions;
    private readonly IOptions<LoginPolicyOptions> _loginPolicy;
    private readonly ILogger<SuperAdminSeeder> _logger;

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "No initial SuperAdmin configured — skipping. Set Seed:SuperAdmin:Email and " +
        "Seed:SuperAdmin:Password (user-secrets locally, environment or Key Vault elsewhere) " +
        "to create one on the next startup.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Initial SuperAdmin '{Email}' created. Sign in and change this password now — " +
        "the configured value is known to whoever configured it.")]
    private static partial void LogCreated(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Information, Message =
        "SuperAdmin '{Email}' already exists — left untouched (the seeder never resets passwords).")]
    private static partial void LogAlreadyExists(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Configured SuperAdmin password rejected by the password policy: {Reason} " +
        "No account was created.")]
    private static partial void LogPasswordRejected(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message =
        "Cannot seed the initial SuperAdmin: the '{RoleName}' role is missing. " +
        "The identity catalog sync must run first.")]
    private static partial void LogRoleMissing(ILogger logger, string roleName);

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Initial SuperAdmin seeding skipped: the database has pending migrations. " +
        "Apply them and restart.")]
    private static partial void LogSkippedPendingMigrations(ILogger logger);

    public SuperAdminSeeder(
        AppDbContext dbContext,
        IPasswordHasher passwordHasher,
        IClock clock,
        IOptions<SuperAdminSeedOptions> seedOptions,
        IOptions<LoginPolicyOptions> loginPolicy,
        ILogger<SuperAdminSeeder> logger)
    {
        _dbContext = dbContext;
        _passwordHasher = passwordHasher;
        _clock = clock;
        _seedOptions = seedOptions;
        _loginPolicy = loginPolicy;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        SuperAdminSeedOptions options = _seedOptions.Value;

        if (!options.IsConfigured)
        {
            LogNotConfigured(_logger);
            return;
        }

        // The same guard PlatformIdentitySeeder applies, and for the same reason: querying
        // platform.users before the migration that creates it throws a bare
        // "relation does not exist", which takes the host down on a first boot with a message that
        // names neither the cause nor the fix. Found by starting the production compose overlay
        // against an empty volume — the identity sync skipped politely and then this crashed.
        if ((await _dbContext.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
        {
            LogSkippedPendingMigrations(_logger);
            return;
        }

        string email = options.Email!.Trim();
        string normalizedEmail = User.Normalize(email);

        if (await _dbContext.Set<User>().AnyAsync(user => user.NormalizedEmail == normalizedEmail, cancellationToken))
        {
            LogAlreadyExists(_logger, email);
            return;
        }

        // The seeded password meets the same bar as one a user would choose: a weak
        // initial credential is not less dangerous for having come from configuration.
        Result policyCheck = PasswordPolicy.Validate(options.Password, _loginPolicy.Value.MinimumPasswordLength);
        if (policyCheck.IsFailure)
        {
            LogPasswordRejected(_logger, policyCheck.Error!.Message);
            return;
        }

        string superAdminRoleName = Role.Normalize(SystemRoles.SuperAdmin);
        Role? superAdminRole = await _dbContext.Set<Role>()
            .SingleOrDefaultAsync(
                role => role.NormalizedName == superAdminRoleName && role.Scope == RoleScope.Platform,
                cancellationToken);

        if (superAdminRole is null)
        {
            LogRoleMissing(_logger, SystemRoles.SuperAdmin);
            return;
        }

        var user = User.Create(email, options.FirstName, options.LastName);
        user.SetPasswordHash(_passwordHasher.Hash(options.Password!), _clock.UtcNow);
        user.AssignRole(superAdminRole.Id);

        _dbContext.Set<User>().Add(user);
        await _dbContext.SaveChangesAsync(cancellationToken);

        LogCreated(_logger, email);
    }
}
