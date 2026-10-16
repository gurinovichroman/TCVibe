using System.IO;
using TCV.App.FileSystem;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Decides whether a delete can really go to the Recycle Bin. Only fixed local drives have one:
/// removable drives, network shares and paths beyond the shell's MAX_PATH are deleted for good,
/// and the confirmation dialog has to say so (INV-11) instead of promising the bin. Search results
/// count as the disk they list.
/// </summary>
public static class RecycleBin
{
    /// <summary>Longest path the shell's delete-to-bin accepts.</summary>
    private const int ShellMaxPath = 259;

    public static bool IsAvailableFor(IVirtualFileSystem fs, VfsPath path)
    {
        // Search hits are disk files under their own paths, listed through a view that forwards every
        // call to the disk (TRAP-28): without looking through it, F8 on them deleted for good.
        if (FileSystemIdentity.Underlying(fs) is not WindowsFileSystem) return false;
        return IsAvailableForWindowsPath(WindowsFileSystem.ToWindowsPath(path));
    }

    public static bool IsAvailableForWindowsPath(string windowsPath)
    {
        if (windowsPath.Length > ShellMaxPath) return false;

        string? root;
        try { root = Path.GetPathRoot(windowsPath); }
        catch (ArgumentException) { return false; }

        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        try { return new DriveInfo(root).DriveType == DriveType.Fixed; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>How many of <paramref name="entries"/> a delete with the bin switched on would really recycle.</summary>
    public static int CountRecyclable(IVirtualFileSystem fs, IEnumerable<IFileEntry> entries) =>
        entries.Count(e => IsAvailableFor(fs, e.Path));
}
