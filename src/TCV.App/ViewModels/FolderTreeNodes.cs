using System.Collections.ObjectModel;
using System.IO;
using TCV.App.Collections;
using TCV.App.Navigation;

namespace TCV.App.ViewModels;

/// <summary>
/// What the panel's Tree view and the docked folder tree do alike with their nodes: read the drive
/// roots, merge a new drive list in, walk down to a path, find the node a listing belongs to.
/// The path arithmetic itself is in <see cref="FolderTree"/>.
/// </summary>
public static class FolderTreeNodes
{
    /// <summary>
    /// Name and root of every ready drive, or null when they could not be read. <c>IsReady</c> asks
    /// every drive, and a sleeping network or optical one takes seconds to answer: off the UI thread
    /// (INV-06). The nodes are created by the caller, back where the tree lives.
    /// </summary>
    public static async Task<IReadOnlyList<(string Name, string Root)>?> ReadDriveRootsAsync()
    {
        try
        {
            var drives = await Task.Run(() => DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => (Label: VolumeLabel(d), d.DriveType, d.Name, Root: d.RootDirectory.FullName))
                .ToList());

            // Named back here: the kind of drive is a string-dictionary word, and the dictionary is the UI thread's.
            return drives.Select(d => (Name: DriveCaption(d.Label, d.DriveType, d.Name), d.Root)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a drive is called in the trees and the side bar, as Explorer calls it: «Work (D:)», or the
    /// kind of drive when it has no label — «Local Disk (C:)».
    /// </summary>
    /// <param name="parentheses">«Local Disk (C:)» in a tree; «Local Disk C:» under the side bar's disk bar.</param>
    public static string DriveCaption(string? label, DriveType type, string name, bool parentheses = true)
    {
        var letter = name.TrimEnd('\\', '/');
        var title = string.IsNullOrWhiteSpace(label)
            ? Localization.LocalizationManager.Get("Drive_Kind_" + type)
            : label.Trim();
        return parentheses ? $"{title} ({letter})" : $"{title} {letter}";
    }

    private static string VolumeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Brings the roots in line with the drive list: a drive that is still there keeps its node (and
    /// whatever is open under it), a new one gets a node from <paramref name="create"/>, a gone one is
    /// removed. Returns true when anything changed.
    /// </summary>
    public static bool MergeRoots(
        ObservableCollection<TreeNodeViewModel> roots,
        IReadOnlyList<(string Name, string Root)> drives,
        Func<string, string, TreeNodeViewModel> create)
    {
        var target = drives
            .Select(d => roots.FirstOrDefault(r => FolderTree.SamePath(r.FullPath, d.Root)) ?? create(d.Name, d.Root))
            .ToList();
        return ListReconciler.Reconcile(roots, target);
    }

    /// <summary>
    /// Opens the tree down to <paramref name="path"/>, loading each level that was never read, and
    /// returns the deepest node reached — the folder itself, or the nearest shown ancestor when part
    /// of the path is not in the tree (a hidden folder). Null when no root holds the path.
    /// </summary>
    /// <param name="expandTarget">Open the folder itself as well, not only the way to it.</param>
    public static async Task<TreeNodeViewModel?> ExpandToAsync(
        IReadOnlyList<TreeNodeViewModel> roots, string path, bool expandTarget)
    {
        var located = FolderTree.Locate(roots.Select(r => r.FullPath).ToList(), path);
        if (located is not { } at)
        {
            return null;
        }

        var current = roots[at.RootIndex];
        for (var i = 0; i < at.Segments.Count; i++)
        {
            await current.ExpandAsync();
            var child = FindChild(current, at.Segments[i]);
            if (child is null)
            {
                return current;
            }

            current = child;
        }

        if (expandTarget)
        {
            await current.ExpandAsync();
        }

        return current;
    }

    /// <summary>
    /// The node of <paramref name="path"/> when every level down to it has been read, without opening
    /// or reading anything; null otherwise. A listing the panel just made is only worth merging into
    /// a node the tree already shows.
    /// </summary>
    public static TreeNodeViewModel? FindLoaded(IReadOnlyList<TreeNodeViewModel> roots, string path)
    {
        var located = FolderTree.Locate(roots.Select(r => r.FullPath).ToList(), path);
        if (located is not { } at)
        {
            return null;
        }

        var current = roots[at.RootIndex];
        foreach (var segment in at.Segments)
        {
            if (!current.IsLoaded || FindChild(current, segment) is not { } child)
            {
                return null;
            }

            current = child;
        }

        return current;
    }

    private static TreeNodeViewModel? FindChild(TreeNodeViewModel parent, string name) =>
        parent.Children.FirstOrDefault(c => c.IsDirectory && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
