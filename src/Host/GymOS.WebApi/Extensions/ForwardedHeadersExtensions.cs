using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace GymOS.WebApi.Extensions;

/// <summary>
/// Recovers the real client IP when the host runs behind a reverse proxy (Azure App Service or
/// Container Apps, AD-11).
///
/// <para><b>Why this matters beyond tidiness.</b> Two features read
/// <c>Connection.RemoteIpAddress</c>: the sign-in rate limiter partitions on it, and audit rows
/// record it. Behind a proxy that address is the proxy's, identical for every caller — which
/// silently collapses the rate limiter into a single shared bucket, so one attacker's traffic
/// throttles every administrator, and makes every audit entry claim the same origin. Neither
/// failure is visible in testing; both appear only in production.</para>
///
/// <para><b>Why it is off by default.</b> <c>X-Forwarded-For</c> is a request header, so any
/// caller can send one. Trusting it without restricting who may set it lets an attacker forge
/// their apparent IP and rotate freely past the per-IP limiter — strictly worse than not reading
/// it at all. It is therefore enabled only when an operator declares the proxy topology, and the
/// declaration is what makes the header trustworthy.</para>
///
/// <code>
/// "Security": {
///   "ForwardedHeaders": {
///     "Enabled": true,
///     "ForwardLimit": 1,              // hops between the client and this app
///     "KnownProxies": [ "10.0.0.4" ], // or:
///     "KnownNetworks": [ "10.0.0.0/16" ]
///   }
/// }
/// </code>
/// With <c>Enabled: true</c> and neither list set, the middleware trusts only loopback — the
/// framework default, and a fail-closed one.
/// </summary>
public static class ForwardedHeadersExtensions
{
    private const string SectionName = "Security:ForwardedHeaders";

    public static IServiceCollection AddGymOSForwardedHeaders(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(SectionName);

        if (!section.GetValue("Enabled", false))
        {
            return services;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // Only the two we actually consume. Enabling X-Forwarded-Host as well would let a
            // caller influence generated URLs, which nothing here needs.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = section.GetValue("ForwardLimit", 1);

            var knownProxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
            var knownNetworks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];

            if (knownProxies.Length == 0 && knownNetworks.Length == 0)
            {
                return; // keep the framework's loopback-only default
            }

            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            foreach (string proxy in knownProxies)
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (string network in knownNetworks)
            {
                string[] parts = network.Split('/', 2);
                options.KnownIPNetworks.Add(
                    new System.Net.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1], CultureInfo.InvariantCulture)));
            }
        });

        return services;
    }

    /// <summary>
    /// Must run before anything that reads the client IP — the rate limiter and, through
    /// <c>ICurrentUser</c>, every audit row.
    /// </summary>
    public static IApplicationBuilder UseGymOSForwardedHeaders(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(configuration);

        if (configuration.GetSection(SectionName).GetValue("Enabled", false))
        {
            app.UseForwardedHeaders();
        }

        return app;
    }
}
