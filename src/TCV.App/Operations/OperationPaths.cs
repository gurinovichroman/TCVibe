using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>Path questions the operations keep asking; pure so they can be tested without a disk.</summary>
public static class OperationPaths
{
    /// <summary>Which file system a destination typed into the copy dialog lives on.</summary>
    public enum TargetSide
    {
        /// <summary>The opposite panel's (what the dialog was prefilled with).</summary>
        Opposite,
        /// <summary>The active panel's: the path was relative to where the user stands.</summary>
        Active,
        /// <summary>The local disk: a Windows path typed while the opposite panel shows a server or archive.</summary>
        LocalDisk,
    }

    /// <summary>
    /// Makes sense of what was typed as an F5/F6 destination. A relative path is relative to the
    /// active panel's folder, as in Total Commander — not to the process's working directory, where
    /// <c>new VfsPath("backup")</c> used to land. A drive or UNC path means the disk even when the
    /// opposite panel is a server; a rooted POSIX path means the opposite panel's server.
    /// </summary>
    public static (string Path, TargetSide Side) ResolveTypedTarget(
        string typed, string activeDirectory, bool activeIsLocal, bool oppositeIsLocal)
    {
        var text = typed.Trim().Trim('"');
        if (text.Length == 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
            text += "\\";   // "D:" alone is drive-relative; the user means the drive

        if (IsWindowsAbsolute(text))
            return (text, oppositeIsLocal ? TargetSide.Opposite : TargetSide.LocalDisk);

        var rooted = text.StartsWith('/') || text.StartsWith('\\');
        if (rooted && !oppositeIsLocal)
            return (text, TargetSide.Opposite);
        if (rooted && !activeIsLocal)
            return (text, TargetSide.Active);

        if (activeIsLocal)
        {
            // "\tmp" is rooted on the active panel's drive, "..\x" is relative to its folder.
            try
            {
                return (Path.GetFullPath(Path.Combine(activeDirectory, text)), TargetSide.Active);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (text, TargetSide.Active);
            }
        }

        return (CollapseDots(activeDirectory.TrimEnd('/') + "/" + text.Replace('\\', '/')), TargetSide.Active);
    }

    /// <summary>
    /// Splits a destination for a copy or move of <paramref name="itemCount"/> items. A single item sent
    /// to a path that is not an existing folder takes that path as its new name (F6 as rename, F5 as
    /// «copy under another name»); several items go into the folder, which is created if missing. A
    /// trailing separator (<paramref name="markedAsFolder"/>) says «a folder» even for one item.
    /// </summary>
    public static (VfsPath Directory, string? NewName) SplitTarget(
        VfsPath target, int itemCount, IFileEntry? existingTarget, bool markedAsFolder = false)
    {
        if (existingTarget is { IsDirectory: true } || markedAsFolder) return (target, null);
        if (itemCount == 1 && target.Parent is { } parent && target.FileName.Length > 0)
            return (parent, target.FileName);
        return (target, null);
    }

    private static bool IsWindowsAbsolute(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);

    private static string CollapseDots(string posixPath)
    {
        var parts = new List<string>();
        foreach (var segment in posixPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return "/" + string.Join('/', parts);
    }

    /// <summary>
    /// A path as the user expects to read it: a local one («C:/src/a.txt» inside the VFS) with
    /// backslashes, anything else (a server or archive path) unchanged.
    /// </summary>
    public static string ToDisplay(string vfsPath)
    {
        var isDrive = vfsPath.Length >= 2 && char.IsAsciiLetter(vfsPath[0]) && vfsPath[1] == ':';
        var isUnc = vfsPath.StartsWith("//", StringComparison.Ordinal);
        return isDrive || isUnc ? vfsPath.Replace('/', '\\') : vfsPath;
    }
    /// <summary>True when <paramref name="candidate"/> is <paramref name="folder"/> or lies below it.</summary>
    public static bool IsSameOrInside(VfsPath candidate, VfsPath folder)
    {
        if (candidate.Equals(folder)) return true;
        var prefix = folder.Value.EndsWith('/') ? folder.Value : folder.Value + "/";
        return candidate.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A directory junction or symlink. Following one can loop forever (pnpm and profile folders
    /// are full of them) and would copy the target's contents as if they lived here.
    /// </summary>
    public static bool IsReparseDirectory(IFileEntry entry) =>
        entry.IsDirectory && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>True when both Windows paths sit on the same volume (a move can be a rename).</summary>
    public static bool SameVolume(string windowsPathA, string windowsPathB)
    {
        try
        {
            return string.Equals(
                Path.GetPathRoot(Path.GetFullPath(windowsPathA)),
                Path.GetPathRoot(Path.GetFullPath(windowsPathB)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
