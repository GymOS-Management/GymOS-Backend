using System.Security.Claims;
using System.Text;
using GymOS.BuildingBlocks.Application.Abstractions;
using GymOS.Platform.Application.Identity.Abstractions;
using GymOS.Platform.Domain.Identity;
using GymOS.Platform.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace GymOS.WebApi.Auth;

/// <summary>
/// Bearer-token validation — the enforcing half of AD-4, wired once here.
///
/// Every validation switch is set explicitly rather than left to the framework's
/// defaults, because the defaults are permissive in exactly the ways that matter:
/// audience validation can be skipped, and the default five-minute clock skew quietly
/// extends a 15-minute token to 20.
/// </summary>
public static class AuthenticationExtensions
{
    public static IServiceCollection AddGymOSAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Claims are read exactly as issued: the legacy inbound mapping would
                // rewrite `sub` to a WS-Federation URI, silently breaking any code that
                // asks for the claim by its RFC 7519 name.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    NameClaimType = GymOSClaims.Subject,
                };

                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = ValidateSecurityStampAsync,
                };
            });

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();

        // The signing key and issuer/audience live in JwtOptions, which is validated at
        // boot; this post-configure step is what keeps one source of truth for both the
        // issuing and validating sides rather than two config sections that can drift.
        services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, JwtBearerOptionsPostConfiguration>();

        services.AddAuthorization();

        return services;
    }

    /// <summary>
    /// Rejects a signed, unexpired token whose account has changed underneath it (AD-4).
    ///
    /// The signature and lifetime checks say the token was genuine when issued; they cannot
    /// say the account is still entitled to it. A password change or a suspension rotates
    /// the user's security stamp, and comparing that here is what turns "sessions die within
    /// one access-token lifetime" into "sessions die now" — which is what an administrator
    /// suspending a compromised account actually expects.
    ///
    /// The read is memoized per request (<c>CallerSnapshot</c>), so the permission checks
    /// that follow reuse it rather than querying again.
    /// </summary>
    private static async Task ValidateSecurityStampAsync(TokenValidatedContext context)
    {
        string? subject = context.Principal?.FindFirstValue(GymOSClaims.Subject);
        string? presentedStamp = context.Principal?.FindFirstValue(GymOSClaims.SecurityStamp);

        if (!Guid.TryParse(subject, out Guid userId) || !Guid.TryParse(presentedStamp, out Guid stamp))
        {
            context.Fail("The token is missing its subject or security stamp.");
            return;
        }

        ICallerSnapshot snapshot = context.HttpContext.RequestServices.GetRequiredService<ICallerSnapshot>();
        UserSecuritySnapshot? current = await snapshot.GetAsync(userId, context.HttpContext.RequestAborted);

        if (current is null)
        {
            context.Fail("The account no longer exists.");
            return;
        }

        if (current.SecurityStamp != stamp)
        {
            context.Fail("The token was invalidated by a password change or suspension.");
            return;
        }

        if (!string.Equals(current.Status, nameof(UserStatus.Active), StringComparison.Ordinal))
        {
            context.Fail("The account is not active.");
        }
    }

    private sealed class JwtBearerOptionsPostConfiguration : IPostConfigureOptions<JwtBearerOptions>
    {
        private readonly IOptions<JwtOptions> _jwtOptions;

        public JwtBearerOptionsPostConfiguration(IOptions<JwtOptions> jwtOptions) => _jwtOptions = jwtOptions;

        public void PostConfigure(string? name, JwtBearerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            JwtOptions jwt = _jwtOptions.Value;
            options.TokenValidationParameters.ValidIssuer = jwt.Issuer;
            options.TokenValidationParameters.ValidAudience = jwt.Audience;
            options.TokenValidationParameters.ClockSkew = jwt.ClockSkew;
            options.TokenValidationParameters.IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey));
        }
    }
}
