using System.IO;

namespace TCV.App.Navigation;

/// <summary>
/// The order a whole-disk name-index walk takes (6.56). The walk is breadth-first and stops at
/// <see cref="NameIndexService.MaxEntries"/>, so what goes missing at the cap is the deep levels —
/// exactly where projects live (<c>D:\Projects\app\src\…</c>). Folders the user works in are walked
/// first, then the places projects usually are, then the rest of the disk: the cap now cuts the long
/// tail of toolchains and games rather than the code the user opens. Pure: paths in, tiers out.
/// </summary>
/// <remarks>
/// A folder inside a priority folder inherits its tier. A folder on the way to one (<c>C:\Users</c> on
/// the way to <c>C:\Users\me\source</c>) is walked early itself, but its other children are not. A
/// priority folder inside a skipped tree (AppData, Program Files) stays unindexed: the walk never
/// enters its parents.
/// </remarks>
public sealed class NameIndexPriority
{
    /// <summary>Folders the user named: workspace tabs, bookmarks, the folders the panels show.</summary>
    public const int Explicit = 0;

    /// <summary>Where projects usually are: the user profile, and <c>X:\Projects</c> and its kin.</summary>
    public const int Likely = 1;

    /// <summary>Everything else.</summary>
    public const int Rest = 2;

    /// <summary>Folder names that, directly under a drive root, are where people keep their code.</summary>
    private static readonly HashSet<string> ProjectFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Projects", "Project", "Repos", "Repositories", "Source", "Sources", "Src", "Dev",
        "Development", "Code", "Git", "GitHub", "Work", "Workspace", "Workspaces",
    };

    // Keys are NameIndexCrawlPolicy.Trim'd: backslashes, no trailing separator ("C:" for a drive root).
    private readonly Dictionary<string, int> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _onPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <c>X:\Projects</c> and its kin count as <see cref="Likely"/>. Off for <see cref="None"/>:
    /// a walk without a priority (a root the user chose, a single-folder refresh) stays plain
    /// breadth-first, rather than spending the cap on one project folder before its siblings.
    /// </summary>
    private readonly bool _promoteProjectFolders;

    private NameIndexPriority(bool promoteProjectFolders) => _promoteProjectFolders = promoteProjectFolders;

    /// <summary>No preference: the walk is plain breadth-first, as before 6.56.</summary>
    public static NameIndexPriority None { get; } = new(promoteProjectFolders: false);

    /// <summary>Builds the order from the user's folders and the profile. Blank, relative, VFS and UNC paths and drive roots are ignored.</summary>
    public static NameIndexPriority Create(IEnumerable<string?> explicitFolders, string? userProfile)
    {
        var priority = new NameIndexPriority(promoteProjectFolders: true);
        foreach (var folder in explicitFolders)
        {
            priority.Add(folder, Explicit);
        }

        priority.Add(userProfile, Likely);
        return priority;
    }

    private void Add(string? folder, int tier)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        // Only a folder on a drive: "D:\x". A drive root is the whole drive, not a place to start from;
        // "/pub" (a server path) and "\\host\share" are never part of the walk.
        var key = NameIndexCrawlPolicy.Trim(folder.Trim());
        if (key.Length < 4 || key[1] != ':' || key[2] != '\\' || !char.IsAsciiLetter(key[0]))
        {
            return;
        }

        _folders[key] = Math.Min(_folders.GetValueOrDefault(key, Rest), tier);
        for (var parent = ParentOf(key); parent is not null; parent = ParentOf(parent))
        {
            _onPath[parent] = Math.Min(_onPath.GetValueOrDefault(parent, Rest), tier);
        }
    }

    /// <summary>
    /// The tier a folder is walked in and the tier it hands down to its children, given the tier its
    /// parent hands down.
    /// </summary>
    public (int Walk, int Inherited) Classify(string path, int parentInherited)
    {
        var key = NameIndexCrawlPolicy.Trim(path);
        var inherited = Math.Min(parentInherited, OwnTier(key));
        var walk = Math.Min(inherited, _onPath.GetValueOrDefault(key, Rest));
        return (walk, inherited);
    }

    /// <summary>As <see cref="Classify"/>, for a walk root: what it inherits comes from the folders above it.</summary>
    public (int Walk, int Inherited) ClassifyRoot(string root)
    {
        var key = NameIndexCrawlPolicy.Trim(root);
        var inherited = Rest;
        for (var parent = ParentOf(key); parent is not null; parent = ParentOf(parent))
        {
            inherited = Math.Min(inherited, OwnTier(parent));
        }

        return Classify(key, inherited);
    }

    private int OwnTier(string key)
    {
        var tier = _folders.GetValueOrDefault(key, Rest);
        if (_promoteProjectFolders && tier > Likely && ProjectFolderNames.Contains(Path.GetFileName(key))
            && ParentOf(key) is { } parent && NameIndexCrawlPolicy.IsDriveRoot(parent))
        {
            tier = Likely;
        }

        return tier;
    }

    private static string? ParentOf(string key) =>
        Path.GetDirectoryName(key) is { Length: > 0 } parent ? NameIndexCrawlPolicy.Trim(parent) : null;
}
