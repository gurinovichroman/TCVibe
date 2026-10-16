namespace TCV.Core.Domain;

/// <summary>
/// A folder tree on the local disk as a developer's trees are: read-only files (git keeps its object
/// store that way) and links (pnpm fills <c>node_modules</c> with junctions). A link belongs to the
/// tree as a link; what it points to is not part of the tree, so nothing here ever enters one.
/// </summary>
public static class DirectoryTree
{
    // Hidden and system entries are part of the tree too: .git is hidden.
    private static readonly EnumerationOptions OneLevel = new() { AttributesToSkip = 0 };

    /// <summary>
    /// Deletes <paramref name="path"/> and everything under it, or only the link when it is one.
    /// <see cref="Directory.Delete(string, bool)"/> alone fails on such a tree twice over: on the first
    /// read-only file or folder (Windows marks a customised folder read-only), and on a junction when the
    /// process is not elevated (TRAP-50). .NET takes a junction for a volume mount point and asks to
    /// unmount it first, which only an administrator may do; it goes on deleting around it, so the files
    /// are gone but the folders above the junction stay, under an "access denied". So the links go one by
    /// one before the tree does.
    /// </summary>
    public static void Delete(string path)
    {
        var root = new DirectoryInfo(path);
        if (IsLink(root))
        {
            root.Delete();
            return;
        }

        ClearReadOnly(root);
        foreach (var (entry, isLink) in Walk(root))
        {
            if (isLink)
            {
                // A file link goes with the tree: deleting it never touches its target.
                if (entry is DirectoryInfo)
                {
                    entry.Delete();
                }
            }
            else
            {
                ClearReadOnly(entry);
            }
        }

        root.Delete(recursive: true);
    }

    /// <summary>Clears the read-only flag of an entry of the tree itself (never a link's).</summary>
    private static void ClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The delete below reports whatever actually blocks it.
        }
    }

    /// <summary>
    /// The first junction or directory symlink at <paramref name="path"/> or under it, or null when the
    /// tree holds none. For whoever must refuse such a tree before touching any of it.
    /// </summary>
    public static string? FindDirectoryLink(string path)
    {
        var root = new DirectoryInfo(path);
        if (IsLink(root))
        {
            return root.FullName;
        }

        foreach (var (entry, isLink) in Walk(root))
        {
            if (isLink && entry is DirectoryInfo)
            {
                return entry.FullName;
            }
        }

        return null;
    }

    /// <summary>Every entry under <paramref name="root"/>, parents first. Links are listed, never entered.</summary>
    private static IEnumerable<(FileSystemInfo Entry, bool IsLink)> Walk(DirectoryInfo root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in List(directory))
            {
                var isLink = IsLink(entry);
                yield return (entry, isLink);
                if (!isLink && entry is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory);
                }
            }
        }
    }

    /// <summary>Listed whole before anything in it changes. An unreadable folder lists as empty: whatever
    /// comes next reports what blocks it.</summary>
    private static FileSystemInfo[] List(DirectoryInfo directory)
    {
        try
        {
            return directory.GetFileSystemInfos("*", OneLevel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// A junction or a symbolic link. Other reparse points (a OneDrive placeholder, a deduplicated
    /// folder) hold this tree's own content and are walked like any folder, as .NET's delete does. One
    /// whose target cannot be read counts as a link: when in doubt, do not enter. Public for every
    /// other walk of a user's tree (packing one into an archive) to draw the same line.
    /// </summary>
    public static bool IsLink(FileSystemInfo entry)
    {
        if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        try
        {
            return entry.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
