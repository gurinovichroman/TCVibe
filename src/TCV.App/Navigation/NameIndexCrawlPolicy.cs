using System.IO;

namespace TCV.App.Navigation;

/// <summary>
/// What the whole-disk name index leaves out (6.56). Pure: a folder's name and its parent's path in,
/// a decision out, so the rules are tested without touching a disk.
/// </summary>
/// <remarks>
/// The index stops at <see cref="NameIndexService.MaxEntries"/>. Program Files, ProgramData and the
/// profile's AppData alone hold hundreds of thousands of entries nobody opens by name; walked like any
/// other folder they used the cap up before the walk got as deep as the user's projects.
/// </remarks>
public static class NameIndexCrawlPolicy
{
    /// <summary>Skipped wherever they are: a recycle bin and the version-control databases.</summary>
    private static readonly HashSet<string> AlwaysSkipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", ".git", ".hg", ".svn",
    };

    /// <summary>
    /// Skipped as direct children of a drive root only: the operating system, installed programs and
    /// Windows' own housekeeping. A folder called "Windows" or "Recovery" inside a project is somebody's
    /// data and stays.
    /// </summary>
    private static readonly HashSet<string> SystemRootFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
        "Recovery", "PerfLogs", "Config.Msi", "MSOCache", "OneDriveTemp",
        "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "$SysReset", "$GetCurrent",
    };

    /// <summary>Dependency folders, skipped anywhere when the walk covers the default whole-disk roots.</summary>
    private static readonly HashSet<string> Vendored = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "__pycache__", ".venv",
    };

    /// <summary>
    /// Skipped as direct children of a user profile (<c>X:\Users\name</c>) on the default roots: the
    /// application data and the package caches and toolchains every developer machine grows there.
    /// </summary>
    private static readonly HashSet<string> ProfileNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", ".nuget", ".gradle", ".m2", ".cargo", ".rustup", ".npm", ".cache", ".dotnet",
        ".vscode", ".vscode-insiders", ".android", ".conda", ".pyenv", "scoop",
    };

    /// <summary>
    /// True when the walk does not enter (or list) the folder <paramref name="name"/> found in
    /// <paramref name="parentPath"/>. <paramref name="wholeDisk"/> is set for the default roots;
    /// a folder the user points the index at on purpose keeps its dependencies and its AppData.
    /// </summary>
    public static bool IsSkipped(string name, string parentPath, bool wholeDisk)
    {
        if (AlwaysSkipped.Contains(name))
        {
            return true;
        }

        if (SystemRootFolders.Contains(name) && IsDriveRoot(parentPath))
        {
            return true;
        }

        if (!wholeDisk)
        {
            return false;
        }

        return Vendored.Contains(name) || (ProfileNoise.Contains(name) && IsUserProfile(parentPath));
    }

    /// <summary>True for <c>C:\</c>, <c>C:</c> and the like.</summary>
    public static bool IsDriveRoot(string path)
    {
        var trimmed = Trim(path);
        return trimmed.Length == 2 && trimmed[1] == ':' && char.IsAsciiLetter(trimmed[0]);
    }

    /// <summary>True for a folder directly under <c>X:\Users</c> — a user profile, the Default one included.</summary>
    public static bool IsUserProfile(string path)
    {
        var users = Path.GetDirectoryName(Trim(path));
        return !string.IsNullOrEmpty(users)
               && string.Equals(Path.GetFileName(users), "Users", StringComparison.OrdinalIgnoreCase)
               && Path.GetDirectoryName(users) is { } root && IsDriveRoot(root);
    }

    /// <summary>The path in one form for comparison: backslashes, no trailing separator.</summary>
    internal static string Trim(string path) => path.Replace('/', '\\').TrimEnd('\\');
}
