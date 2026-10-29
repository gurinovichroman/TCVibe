using System.IO;
using System.Text.Json;

namespace TCV.App.Workspaces;

/// <summary>
/// Looks for the picture a project already has — its logo, its favicon, the icon JetBrains or VS Code
/// show for it — so the rail can show the project as its authors drew it. Only inside a git repository:
/// a home or a downloads folder has no logo, only a great many pictures.
/// </summary>
/// <remarks>
/// Which file is the best is decided by its path alone (<see cref="Score"/>): a logo before an icon,
/// an icon before a favicon, a shallow file before a deep one, and nothing from tests, samples or
/// dependencies. The walk is bounded in depth and in folders: the search runs for every workspace
/// without a picture, in the background (INV-06), and must stay cheap on a large monorepo.
/// </remarks>
public static class ProjectIconFinder
{
    /// <summary>How many candidates the properties window offers.</summary>
    public const int MaxCandidates = 8;

    private const int MaxDepth = 4;
    private const int MaxFolders = 3000;
    private const long MaxFileBytes = 4 * 1024 * 1024;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".svg", ".ico", ".jpg", ".jpeg", ".gif", ".bmp",
    };

    /// <summary>Folders of dependencies, build output and caches: their logos are somebody else's.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages", "venv",
        "__pycache__", "coverage", "Pods", "DerivedData", "artifacts", "TestResults", "third_party",
        "third-party", "external", "deps", "bower_components", "jspm_packages",
    };

    /// <summary>Hidden folders that do hold a project's own pictures.</summary>
    private static readonly HashSet<string> VisitedHiddenFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".gitlab", ".idea",
    };

    /// <summary>Folders where a project keeps its pictures: a file there is likelier to be its own.</summary>
    private static readonly HashSet<string> PictureFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "assets", "images", "img", "media", "branding", "brand", "logo", "logos", "icons", "public",
        "static", "wwwroot", "resources", "res", "art", ".github",
    };

    /// <summary>Folders whose pictures are data, not the project's face.</summary>
    private static readonly HashSet<string> DataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "tests", "testdata", "fixtures", "samples", "sample", "examples", "example", "demo", "demos",
    };

    /// <summary>Words in a file name that mark a picture which is not square or not the mark itself.</summary>
    private static readonly HashSet<string> WideWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "banner", "wordmark", "horizontal", "wide", "text", "social", "og", "screenshot", "preview", "cover", "header",
    };

    /// <summary>The working tree a folder belongs to (the folder holding <c>.git</c>), or null outside one.</summary>
    public static string? FindRepositoryRoot(string folder)
    {
        try
        {
            for (var dir = new DirectoryInfo(folder); dir is not null; dir = dir.Parent)
            {
                var git = Path.Combine(dir.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    return dir.FullName;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable or malformed path: no repository to look in.
        }

        return null;
    }

    /// <summary>
    /// The project's pictures under <paramref name="root"/>, best first, at most <see cref="MaxCandidates"/>.
    /// Full paths.
    /// </summary>
    public static IReadOnlyList<string> Find(string root, CancellationToken ct = default)
    {
        var found = new List<(string Path, int Score, int Length)>();

        void Consider(string fullPath, int? score)
        {
            if (score is { } s && IsSmallEnough(fullPath))
            {
                found.Add((fullPath, s, fullPath.Length));
            }
        }

        if (PackageIcon(root) is { } declared)
        {
            Consider(declared, DeclaredScore);
        }

        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < MaxFolders)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = pending.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    Consider(file, Score(Relative(root, file)));
                }

                if (depth >= MaxDepth)
                {
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    var hidden = name.StartsWith('.') && !VisitedHiddenFolders.Contains(name);
                    if (!hidden && !SkippedFolders.Contains(name) && !IsLink(sub))
                    {
                        pending.Enqueue((sub, depth + 1));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed holds no candidates.
            }
        }

        return found
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(f => f.Score))
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Length)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(MaxCandidates)
            .Select(f => f.Path)
            .ToList();
    }

    /// <summary>The score of the picture a manifest names as the project's own (VS Code's package.json "icon").</summary>
    private const int DeclaredScore = 95;

    /// <summary>
    /// How likely the file at <paramref name="relativePath"/> (from the repository root, '/' between
    /// folders) is the project's own picture; null when it is not a candidate at all.
    /// </summary>
    public static int? Score(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var file = segments[^1];
        var extension = Path.GetExtension(file);
        if (!ImageExtensions.Contains(extension))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        var folders = segments[..^1];
        var words = name.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);

        int score;
        if (folders is [var only] && only.Equals(".idea", StringComparison.OrdinalIgnoreCase) && name is "icon" or "icon_dark")
        {
            score = name == "icon" ? 100 : 96;   // JetBrains' own project icon
        }
        else if (name.Contains("logo", StringComparison.Ordinal))
        {
            score = 90;
        }
        else if (name is "icon" or "appicon" or "app-icon" or "app_icon" or "app" or "mark")
        {
            score = 75;
        }
        else if (name.StartsWith("favicon", StringComparison.Ordinal))
        {
            score = 70;
        }
        else if (name.StartsWith("apple-touch-icon", StringComparison.Ordinal))
        {
            score = 65;
        }
        else if (name.StartsWith("android-chrome", StringComparison.Ordinal) || name is "icon-512" or "icon-192" or "icon512")
        {
            score = 62;
        }
        else if (name == "ic_launcher")
        {
            score = folders.Any(f => f.Equals("mipmap-xxxhdpi", StringComparison.OrdinalIgnoreCase)) ? 55 : 45;
        }
        else if (extension.Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            score = 50;
        }
        else
        {
            return null;
        }

        if (words.Any(WideWords.Contains))
        {
            score -= 25;
        }

        if (folders.Any(DataFolders.Contains))
        {
            score -= 30;
        }

        if (folders.Any(PictureFolders.Contains))
        {
            score += 5;
        }

        score -= 3 * folders.Length;
        score += extension.ToLowerInvariant() switch
        {
            ".svg" => 3,   // sharp at any size
            ".png" => 2,
            ".ico" => 1,
            _ => 0,
        };

        return score;
    }

    /// <summary>The picture package.json declares as the project's icon (a VS Code extension's), if it exists.</summary>
    private static string? PackageIcon(string root)
    {
        try
        {
            var manifest = Path.Combine(root, "package.json");
            if (!File.Exists(manifest) || new FileInfo(manifest).Length > 1024 * 1024)
            {
                return null;
            }

            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("icon", out var icon)
                && icon.ValueKind == JsonValueKind.String
                && icon.GetString() is { Length: > 0 } relative
                && ImageExtensions.Contains(Path.GetExtension(relative)))
            {
                // Inside the repository, separator included: «../repo-other/logo.png» starts with «…\repo» too.
                var full = Path.GetFullPath(Path.Combine(root, relative));
                var inside = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
                return full.StartsWith(inside, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? full : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            // An unreadable manifest declares nothing.
        }

        return null;
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path);

    private static bool IsSmallEnough(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            return length > 0 && length <= MaxFileBytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A junction or symbolic link: followed, it could loop or leave the repository.</summary>
    private static bool IsLink(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>When a workspace's picture is looked for, and where.</summary>
public static class WorkspacePicture
{
    /// <summary>True while the workspace has no picture and no emoji and the user has not decided.</summary>
    public static bool WantsSearch(Workspace workspace) =>
        workspace.AutoIconImage
        && string.IsNullOrWhiteSpace(workspace.IconImage)
        && string.IsNullOrWhiteSpace(workspace.Icon);

    /// <summary>
    /// The folder the project lives in: the active tab of the panel that had focus, else any tab — a
    /// folder on a local or network disk, never an archive or a server address.
    /// </summary>
    public static string? ProjectFolder(Workspace workspace)
    {
        var first = workspace.ActiveLeft ? workspace.Left : workspace.Right;
        var second = workspace.ActiveLeft ? workspace.Right : workspace.Left;
        return Tabs(first, true).Concat(Tabs(second, true)).Concat(Tabs(first, false)).Concat(Tabs(second, false))
            .FirstOrDefault(IsDiskFolder);

        static IEnumerable<string> Tabs(WorkspacePanelState panel, bool activeOnly)
        {
            var tabs = panel.Tabs ?? [];
            if (activeOnly)
            {
                return panel.ActiveTabIndex >= 0 && panel.ActiveTabIndex < tabs.Count
                    ? [tabs[panel.ActiveTabIndex].Path]
                    : [];
            }

            return tabs.Select(t => t.Path);
        }
    }

    /// <summary>
    /// The folder the workspace's active panel was left in (its active tab) when that is a folder on a
    /// disk, else null. Unlike <see cref="ProjectFolder"/> it never falls back to another tab or the
    /// other panel: the workspace terminal of a workspace that is not on screen runs its command here,
    /// as it runs in the live active panel's folder for the one that is, and a server tab is no folder.
    /// </summary>
    public static string? ActivePanelFolder(Workspace workspace)
    {
        var panel = workspace.ActiveLeft ? workspace.Left : workspace.Right;
        var tabs = panel.Tabs ?? [];
        var path = panel.ActiveTabIndex >= 0 && panel.ActiveTabIndex < tabs.Count
            ? tabs[panel.ActiveTabIndex].Path
            : null;
        return IsDiskFolder(path) ? path : null;
    }

    private static bool IsDiskFolder(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && !path.Contains("://", StringComparison.Ordinal)
        && Path.IsPathFullyQualified(path);
}
