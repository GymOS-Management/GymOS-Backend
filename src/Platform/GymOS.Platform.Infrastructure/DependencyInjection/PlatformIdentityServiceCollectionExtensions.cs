using System.Text;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Auditing;
using GymOS.Platform.Application.Identity;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Infrastructure.Email;
using GymOS.Platform.Infrastructure.Identity;
using GymOS.Platform.Infrastructure.Persistence;
using GymOS.Platform.Infrastructure.Persistence.Queries;
using GymOS.Platform.Infrastructure.Persistence.Repositories;
using GymOS.Platform.Infrastructure.Persistence.Seeding;
using GymOS.Platform.Infrastructure.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GymOS.Platform.Infrastructure.DependencyInjection;

/// <summary>
/// Composes the identity adapters (AD-4) and binds their options.
///
/// Every options block is validated <c>OnStart</c>: a misconfigured signing key or an
/// absurd Argon2 parameter must stop the host at boot, where it is one clear message,
/// rather than at the first sign-in attempt, where it is an outage (AD-45 declare-and-validate).
/// </summary>
public static class PlatformIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformIdentity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<LoginPolicyOptions>()
            .Bind(configuration.GetSection(LoginPolicyOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<PasswordHashingOptions>()
            .Bind(configuration.GetSection(PasswordHashingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty)
                    >= JwtOptions.MinimumSigningKeyBytes,
                $"{JwtOptions.SectionName}:SigningKey must be at least {JwtOptions.MinimumSigningKeyBytes} " +
                "bytes. Generate one with: dotnet user-secrets set \"Security:Jwt:SigningKey\" \"$(openssl rand -base64 48)\"")
            .ValidateOnStart();

        services.AddOptions<SuperAdminSeedOptions>()
            .Bind(configuration.GetSection(SuperAdminSeedOptions.SectionName))
            .ValidateOnStart();

        // ADR-53. Validated at startup rather than at first send, and only when enabled: a
        // misconfigured mail server that reveals itself when somebody invites an administrator is a
        // defect found by a person waiting for an email. Off by default, so the integration suite and
        // a developer without the Mailpit profile need no SMTP server.
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.Smtp.Host),
                $"{EmailOptions.SectionName}:Smtp:Host is required when {EmailOptions.SectionName}:Enabled is true.")
            .Validate(
                options => !options.Enabled || !string.IsNullOrWhiteSpace(options.From.Address),
                $"{EmailOptions.SectionName}:From:Address is required when {EmailOptions.SectionName}:Enabled is true.")
            .Validate(
                options => !options.Enabled || Uri.TryCreate(options.ConsoleBaseUrl, UriKind.Absolute, out _),
                $"{EmailOptions.SectionName}:ConsoleBaseUrl must be an absolute URL when " +
                $"{EmailOptions.SectionName}:Enabled is true — every activation link is built from it, " +
                "and a wrong one is already delivered before anyone notices.")
            .ValidateOnStart();

        services.TryAddScoped<IPasswordHasher, Argon2PasswordHasher>();
        services.TryAddScoped<IAccessTokenIssuer, JwtTokenService>();
        services.TryAddScoped<IRefreshTokenGenerator, RefreshTokenGenerator>();
        services.TryAddScoped<IActivationTokenGenerator, ActivationTokenGenerator>();

        // The transport is chosen per call from the live Email settings (M7 spec §4), not once at
        // start-up, so a change to Email:Enabled takes effect without a restart (ADR-53).
        services.TryAddScoped<SmtpEmailSender>();
        services.TryAddScoped<LoggingEmailSender>();
        services.TryAddScoped<IEmailSender, SwitchingEmailSender>();

        services.TryAddScoped<IActivationLinks, ActivationLinks>();

        services.TryAddScoped<IUserRepository, UserRepository>();
        services.TryAddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.TryAddScoped<IUserActivationTokenRepository, UserActivationTokenRepository>();
        services.TryAddScoped<IRoleRepository, RoleRepository>();
        services.TryAddScoped<IIdentityQueries, IdentityQueries>();
        services.TryAddScoped<IUserQueries, UserQueries>();
        services.TryAddScoped<IRoleQueries, RoleQueries>();
        services.TryAddScoped<IAuditLogQueries, AuditLogQueries>();
        services.TryAddScoped<IUnitOfWork, UnitOfWork>();

        // Scoped: the snapshot memoizes the caller's permissions and security stamp for one
        // request, so the token validator, the endpoint policy, and the pipeline behavior
        // share a single read (AD-22).
        services.TryAddScoped<ICallerSnapshot, CallerSnapshot>();
        services.TryAddScoped<IPermissionEvaluator, PermissionEvaluator>();

        services.TryAddScoped<SuperAdminSeeder>();

        return services;
    }
}
