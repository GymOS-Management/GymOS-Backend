namespace GymOS.ArchitectureTests;

/// <summary>
/// Locates the backend root (the directory containing the solution file) by walking
/// up from the test output directory, so csproj-level rules can inspect the real
/// project files regardless of where the tests execute (IDE, CLI, CI).
/// </summary>
internal static class SolutionLocator
{
    public static DirectoryInfo BackendRoot { get; } = Find();

    private static DirectoryInfo Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !ContainsSolution(directory))
        {
            directory = directory.Parent;
        }

        return directory ?? throw new InvalidOperationException(
            "No GymOS solution file found in any directory above the test output directory.");
    }

    private static bool ContainsSolution(DirectoryInfo directory) =>
        directory.EnumerateFiles("*.slnx").Any() ||
        directory.EnumerateFiles("*.sln").Any();
}
