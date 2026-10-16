using System.IO;
using TCV.Core.Interfaces;

namespace TCV.App.Navigation;

/// <summary>A child a folder shows in a tree: its name and whether it is a folder.</summary>
public readonly record struct FolderTreeEntry(string Name, bool IsDirectory);

/// <summary>
/// The path logic of the two folder trees — the panel's Tree view and the tree docked to the left
/// of the panels — kept apart from the nodes so it can be tested without a disk or a window. Both
/// trees show the local disk only: one root per ready drive, Windows paths throughout.
/// </summary>
public static class FolderTree
{
    /// <summary>
    /// Which root <paramref name="path"/> lies under and the folder names leading down to it from
    /// there, or null when no root holds it (a UNC path, a path on a drive that is not listed). The
    /// match respects folder boundaries: root <c>C:\a</c> does not hold <c>C:\ab</c>.
    /// </summary>
    public static (int RootIndex, IReadOnlyList<string> Segments)? Locate(IReadOnlyList<string> rootPaths, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var best = -1;
        var bestLength = -1;
        for (var i = 0; i < rootPaths.Count; i++)
        {
            var root = Trim(rootPaths[i]);
            if (root.Length > bestLength && Holds(root, path))
            {
                best = i;
                bestLength = root.Length;
            }
        }

        if (best < 0)
        {
            return null;
        }

        var segments = path[bestLength..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return (best, segments);
    }

    /// <summary>True when the two strings name the same folder: case and a trailing separator do not count.</summary>
    public static bool SamePath(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What a folder shows for a listing: folders, then files unless <paramref name="foldersOnly"/>,
    /// each group by name. Hidden entries are left out, as they always were in the tree.
    /// </summary>
    public static IReadOnlyList<FolderTreeEntry> ChildrenOf(IEnumerable<IFileEntry> listing, bool foldersOnly)
    {
        var visible = listing.Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden)).ToList();
        var folders = visible
            .Where(e => e.IsDirectory)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new FolderTreeEntry(e.Name, true));
        if (foldersOnly)
        {
            return folders.ToList();
        }

        var files = visible
            .Where(e => !e.IsDirectory)
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new FolderTreeEntry(e.Name, false));
        return folders.Concat(files).ToList();
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private static bool Holds(string trimmedRoot, string path) =>
        trimmedRoot.Length > 0
        && path.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase)
        && (path.Length == trimmedRoot.Length || path[trimmedRoot.Length] is '\\' or '/');
}
