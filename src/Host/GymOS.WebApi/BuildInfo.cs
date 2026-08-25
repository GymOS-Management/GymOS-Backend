using System.Reflection;

namespace GymOS.WebApi;

/// <summary>
/// What this build is, read once from the assembly's own attributes.
///
/// <para><b>Nothing here is computed at runtime and nothing is invented.</b> The version comes from
/// <c>VersionPrefix</c> in <c>Directory.Build.props</c>; the commit comes from
/// <c>SourceRevisionId</c>, which the SDK appends to <c>AssemblyInformationalVersion</c> after a
/// <c>+</c>; the timestamp comes from an <c>AssemblyMetadata</c> item the build supplies. Each is
/// <see langword="null"/> or empty when the build did not stamp it — a developer's local build
/// reports no timestamp rather than a misleading one.</para>
///
/// <para><b>The commit is the identity that matters.</b> CI tags the image with the same SHA it
/// stamps here, so the value <c>/version</c> reports is the image tag, which is the artifact
/// Production promotes. That chain is what makes a running container traceable to a commit, and it
/// is the reason <c>docs/runbooks/DEPLOYMENT-ROLLBACK.md</c> can name a rollback target precisely.</para>
/// </summary>
internal static class BuildInfo
{
    /// <summary>Raw "0.9.0+&lt;sha&gt;" as the SDK assembled it, or empty if the attribute is absent.</summary>
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? string.Empty;

    /// <summary>Index of the SourceRevisionId separator, or -1 when the build stamped no commit.</summary>
    private static readonly int RevisionSeparator = Informational.IndexOf('+', StringComparison.Ordinal);

    /// <summary>The assembly version without the commit suffix — "0.9.0".</summary>
    public static string Version { get; } =
        RevisionSeparator < 0 ? Informational : Informational[..RevisionSeparator];

    /// <summary>
    /// The full commit SHA, or <see langword="null"/> when unstamped. Full rather than
    /// abbreviated on purpose: this has to match the container image tag character for character,
    /// and an abbreviation would need reconciling by whoever is reading it during an incident.
    /// </summary>
    public static string? Commit { get; } =
        RevisionSeparator >= 0 && RevisionSeparator < Informational.Length - 1
            ? Informational[(RevisionSeparator + 1)..]
            : null;

    /// <summary>
    /// When the build ran, ISO-8601 UTC, or <see langword="null"/> for a build that supplied no
    /// timestamp. Read from assembly metadata rather than the clock, because a value read at start-up
    /// would report when the container booted — which is not a build fact and is already in the logs.
    /// </summary>
    public static string? BuildTimestampUtc { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => string.Equals(metadata.Key, "BuildTimestampUtc", StringComparison.Ordinal))
            ?.Value is { Length: > 0 } stamped
            ? stamped
            : null;
}
