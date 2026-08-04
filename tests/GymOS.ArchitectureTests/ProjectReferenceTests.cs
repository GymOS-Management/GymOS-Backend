using System.Xml.Linq;
using Xunit;

namespace GymOS.ArchitectureTests;

/// <summary>
/// Project-level dependency law: every production csproj must appear in the
/// allowed-graph table below, and may reference only what the table permits.
/// Adding a project without declaring its place in the architecture fails the
/// build by design (declare-and-validate, AD-16/AD-49).
/// </summary>
public sealed class ProjectReferenceTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences =
        new(StringComparer.Ordinal)
        {
            ["GymOS.SharedKernel"] = [],
            ["GymOS.BuildingBlocks.Application"] = ["GymOS.SharedKernel"],
            ["GymOS.BuildingBlocks.Infrastructure"] = ["GymOS.BuildingBlocks.Application"],
            ["GymOS.Platform.Domain"] = ["GymOS.SharedKernel"],
            ["GymOS.Platform.Application"] = ["GymOS.Platform.Domain", "GymOS.BuildingBlocks.Application"],
            ["GymOS.Platform.Infrastructure"] = ["GymOS.Platform.Application", "GymOS.BuildingBlocks.Infrastructure"],
            ["GymOS.WebApi"] = ["GymOS.Platform.Infrastructure", "GymOS.BuildingBlocks.Infrastructure"],
        };

    [Fact]
    public void EveryProductionProject_IsDeclaredInTheDependencyRuleTable()
    {
        var undeclared = ProductionProjects()
            .Select(project => Path.GetFileNameWithoutExtension(project.Name))
            .Where(name => !AllowedReferences.ContainsKey(name))
            .ToList();

        Assert.True(
            undeclared.Count == 0,
            "Projects missing from the AllowedReferences table (declare their place in the " +
            $"architecture before they can build): {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void EveryProductionProject_ReferencesOnlyItsAllowedDependencies()
    {
        var violations = new List<string>();

        foreach (FileInfo project in ProductionProjects())
        {
            string name = Path.GetFileNameWithoutExtension(project.Name);
            if (!AllowedReferences.TryGetValue(name, out string[]? allowed))
            {
                continue; // reported by the declaration test
            }

            IEnumerable<string> forbidden = ReferencedProjectNames(project)
                .Where(reference => !allowed.Contains(reference, StringComparer.Ordinal));

            violations.AddRange(forbidden.Select(reference => $"{name} → {reference}"));
        }

        Assert.True(
            violations.Count == 0,
            "Forbidden project references: " + string.Join("; ", violations));
    }

    private static IEnumerable<FileInfo> ProductionProjects()
    {
        var sourceRoot = new DirectoryInfo(Path.Combine(SolutionLocator.BackendRoot.FullName, "src"));
        return sourceRoot.EnumerateFiles("*.csproj", SearchOption.AllDirectories);
    }

    private static IEnumerable<string> ReferencedProjectNames(FileInfo project) =>
        XDocument.Load(project.FullName)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(include => include is not null)
            .Select(include => Path.GetFileNameWithoutExtension(include!));
}
