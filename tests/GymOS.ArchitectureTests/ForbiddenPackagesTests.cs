using System.Xml.Linq;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Dependency law: enforces docs/Engineering/DEPENDENCY_POLICY.md at build time.
/// The policy tables below and that document must change together, in the same
/// commit, with rationale recorded in the document.
/// </summary>
public sealed class ForbiddenPackagesTests
{
    // ── Policy tables (mirror of DEPENDENCY_POLICY.md) ──────────────────────────

    /// <summary>Exact package ids approved for use (current + pre-approved by the frozen spec).</summary>
    private static readonly HashSet<string> ApprovedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        // In use now
        "Microsoft.NET.Test.Sdk",
        "NetArchTest.Rules",
        "xunit",
        "xunit.runner.visualstudio",

        // Pre-approved, enter with their milestone
        "Npgsql",
        "Npgsql.EntityFrameworkCore.PostgreSQL",
        "EFCore.NamingConventions",
        "FluentValidation",
        "FluentValidation.DependencyInjectionExtensions",
        "Polly",
        "Konscious.Security.Cryptography.Argon2",
        "Asp.Versioning.Http",
        "NSubstitute",
    };

    /// <summary>Approved package families (prefix match, pre-approved by the frozen spec).</summary>
    private static readonly string[] ApprovedPrefixes =
    [
        "Microsoft.Extensions.",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore.",
        "Serilog",
        "OpenTelemetry",
        "Testcontainers",
        "Azure.",
    ];

    /// <summary>Banned packages (prefix match) with the reason surfaced in the failure message.</summary>
    private static readonly Dictionary<string, string> BannedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MediatR"] = "commercial license from v13 (AD-1) — use the in-house command/query dispatcher",
        ["AutoMapper"] = "commercial license from v15 (AD-1) — use explicit ToDto() mapping",
        ["Newtonsoft.Json"] = "System.Text.Json is the platform standard — approval requires an explicit ADR",
        ["FluentAssertions"] = "commercial license from v8 — use xunit assertions",
        ["Moq"] = "not approved — use NSubstitute",
        ["Hangfire"] = "LGPL-3.0/commercial — requires a license ADR before the AD-23 adapter decision",
        ["MassTransit"] = "commercial licensing from v9 — the outbox (AD-13) is our broker seam",
    };

    /// <summary>
    /// Provider/vendor SDKs (prefix match): permitted only in Infrastructure projects,
    /// the WebApi host, and tests (rule R8 — no vendor types outside adapters).
    /// </summary>
    private static readonly string[] ProviderSdkPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "EFCore.",
        "Azure.",
        "AWSSDK.",
        "Stripe",
        "Twilio",
        "SendGrid",
        "MailKit",
        "StackExchange.Redis",
        "Anthropic",
        "OpenAI",
        "Konscious.",
    ];

    /// <summary>The only packages an Application-layer project may reference.</summary>
    private static readonly string[] ApplicationLayerAllowed =
    [
        "Microsoft.Extensions.Options",
        "FluentValidation",
        "FluentValidation.DependencyInjectionExtensions",
    ];

    private static readonly string[] ApplicationLayerAllowedPrefixes =
    [
        "Microsoft.Extensions.", // narrowed to *.Abstractions below
    ];

    // ── Rules ───────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryCentrallyManagedPackage_IsOnTheApprovedList()
    {
        var packagesProps = new FileInfo(
            Path.Combine(SolutionLocator.BackendRoot.FullName, "Directory.Packages.props"));

        var unapproved = XDocument.Load(packagesProps.FullName)
            .Descendants("PackageVersion")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Where(id => !IsApproved(id))
            .ToList();

        Assert.True(
            unapproved.Count == 0,
            "Packages not on the approved list (see docs/Engineering/DEPENDENCY_POLICY.md — " +
            $"approve there and here, in the same commit): {string.Join(", ", unapproved)}");
    }

    [Fact]
    public void NoProject_ReferencesABannedPackage()
    {
        var violations = new List<string>();

        foreach ((FileInfo project, string package) in AllPackageReferences())
        {
            string? banKey = BannedPackages.Keys.FirstOrDefault(
                key => package.StartsWith(key, StringComparison.OrdinalIgnoreCase));

            if (banKey is not null)
            {
                violations.Add(
                    $"{ProjectName(project)} references {package}: {BannedPackages[banKey]}");
            }
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void DomainLayerProjects_HaveNoPackageReferencesAtAll()
    {
        var violations = AllPackageReferences()
            .Where(entry => IsDomainLayer(ProjectName(entry.Project)))
            .Select(entry => $"{ProjectName(entry.Project)} → {entry.Package}")
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Domain-layer projects depend on the language, nothing else (policy rule 3): " +
            string.Join(", ", violations));
    }

    [Fact]
    public void ApplicationLayerProjects_ReferenceOnlyApprovedAbstractions()
    {
        var violations = new List<string>();

        foreach ((FileInfo project, string package) in AllPackageReferences())
        {
            if (!IsApplicationLayer(ProjectName(project)))
            {
                continue;
            }

            bool allowedExact = ApplicationLayerAllowed.Contains(package, StringComparer.OrdinalIgnoreCase);
            bool allowedAbstraction =
                ApplicationLayerAllowedPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) &&
                package.EndsWith(".Abstractions", StringComparison.OrdinalIgnoreCase);

            if (!allowedExact && !allowedAbstraction)
            {
                violations.Add($"{ProjectName(project)} → {package}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Application layers may reference abstractions only (policy rule 3): " +
            string.Join(", ", violations));
    }

    [Fact]
    public void ProviderSdks_AppearOnlyInInfrastructureHostOrTests()
    {
        var violations = new List<string>();

        foreach ((FileInfo project, string package) in AllPackageReferences())
        {
            string name = ProjectName(project);
            bool isProviderSdk = ProviderSdkPrefixes.Any(
                prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

            if (isProviderSdk && !IsInfrastructureHostOrTest(project, name))
            {
                violations.Add($"{name} → {package}");
            }
        }

        Assert.True(
            violations.Count == 0,
            "Provider SDKs live in adapters only (rule R8): " + string.Join(", ", violations));
    }

    [Fact]
    public void NoPackageReference_BypassesCentralPackageManagement()
    {
        var violations = AllProjects()
            .SelectMany(project => XDocument.Load(project.FullName)
                .Descendants("PackageReference")
                .Where(element =>
                    element.Attribute("Version") is not null ||
                    element.Attribute("VersionOverride") is not null)
                .Select(element =>
                    $"{ProjectName(project)} → {element.Attribute("Include")?.Value}"))
            .ToList();

        Assert.True(
            violations.Count == 0,
            "Versions belong in Directory.Packages.props only (policy rule 4): " +
            string.Join(", ", violations));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool IsApproved(string packageId) =>
        ApprovedPackages.Contains(packageId) ||
        ApprovedPrefixes.Any(prefix => packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool IsDomainLayer(string projectName) =>
        projectName is "GymOS.SharedKernel" ||
        projectName.EndsWith(".Domain", StringComparison.Ordinal) ||
        projectName.EndsWith(".Contracts", StringComparison.Ordinal);

    private static bool IsApplicationLayer(string projectName) =>
        projectName.EndsWith(".Application", StringComparison.Ordinal);

    private static bool IsInfrastructureHostOrTest(FileInfo project, string projectName) =>
        projectName.EndsWith(".Infrastructure", StringComparison.Ordinal) ||
        projectName is "GymOS.WebApi" ||
        project.FullName.Contains(
            Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string ProjectName(FileInfo project) =>
        Path.GetFileNameWithoutExtension(project.Name);

    private static readonly string[] ScannedFolders = ["src", "tests"];

    private static IEnumerable<FileInfo> AllProjects()
    {
        DirectoryInfo root = SolutionLocator.BackendRoot;
        return ScannedFolders
            .Select(folder => new DirectoryInfo(Path.Combine(root.FullName, folder)))
            .Where(directory => directory.Exists)
            .SelectMany(directory => directory.EnumerateFiles("*.csproj", SearchOption.AllDirectories));
    }

    private static IEnumerable<(FileInfo Project, string Package)> AllPackageReferences() =>
        AllProjects().SelectMany(project => XDocument.Load(project.FullName)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .OfType<string>()
            .Select(package => (project, package)));
}
