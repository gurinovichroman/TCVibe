using System.IO;

namespace TCV.App.Tests;

/// <summary>
/// The checkout the tests run from: the folder that holds the solution file. Kept in one place, so a
/// renamed solution is one edit, and found on first use, so a test that reads no repository file does
/// not fail because its class also has tests that do.
/// </summary>
internal static class SourceTree
{
    private const string SolutionFile = "TCVibe.slnx";

    private static readonly Lazy<string> RootPath = new(Find);

    /// <summary>Absolute path of the repository root.</summary>
    public static string Root => RootPath.Value;

    /// <summary><c>src\TCV.App</c>, where the guarded XAML and dictionaries live.</summary>
    public static string AppDirectory => Path.Combine(Root, "src", "TCV.App");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, SolutionFile)))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException($"Repository root ({SolutionFile}) not found.");
    }
}
