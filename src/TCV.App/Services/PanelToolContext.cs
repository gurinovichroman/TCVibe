using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Services;

/// <summary>
/// The application's implementation of <see cref="IToolContext"/> — the Plugin SDK's view of "what
/// the user is looking at right now": the selection, both panels' file systems and paths, and a way
/// to reload after the tool has run (task 6.29).
/// </summary>
/// <remarks>
/// A snapshot, not a live view: it is built when a tool is launched and handed to it, so a tool that
/// takes its time cannot have the panel navigate out from under its macros. <see cref="Refresh"/> is
/// the one thing that reaches back into the live panel, because reloading a folder the user has since
/// left would be worse than reloading the one they are in.
/// </remarks>
public sealed class PanelToolContext : IToolContext
{
    private readonly PanelViewModel _active;

    private PanelToolContext(
        PanelViewModel active,
        IReadOnlyList<IFileEntry> selected,
        IVirtualFileSystem activeFs, VfsPath activePath,
        IVirtualFileSystem oppositeFs, VfsPath oppositePath)
    {
        _active = active;
        SelectedFiles = selected;
        ActiveFileSystem = activeFs;
        ActivePath = activePath;
        OppositeFileSystem = oppositeFs;
        OppositePath = oppositePath;
    }

    /// <summary>Captures the current context of the two panels.</summary>
    public static PanelToolContext Capture(
        PanelViewModel active, PanelViewModel opposite, IReadOnlyList<IFileEntry> selected)
    {
        var (activeFs, activePath) = FolderOf(active.FileList, asTarget: false);
        var (oppositeFs, oppositePath) = FolderOf(opposite.FileList, asTarget: true);
        return new(active, selected, activeFs, activePath, oppositeFs, oppositePath);
    }

    /// <summary>
    /// A panel as a tool sees it. One with a folder on the disk is reported as the disk and that
    /// folder: the active panel where it works (<see cref="FileListViewModel.WorkingFolder"/> — the
    /// search-results list works in the folder it was opened over), the other where something would
    /// be put (<see cref="FileListViewModel.DiskFolder"/> — %T is a target, and the list is no place to
    /// put anything). Any other panel keeps its mount and the mount's own path, which the macros
    /// refuse rather than read as a Windows path (TRAP-28).
    /// </summary>
    public static (IVirtualFileSystem Fs, VfsPath Path) FolderOf(FileListViewModel list, bool asTarget)
    {
        var folder = asTarget ? list.DiskFolder : list.WorkingFolder;
        return folder is not null
            ? (list.DiskVfs, new VfsPath(folder))
            : (list.ActiveVfs, new VfsPath(list.CurrentPath));
    }

    public IReadOnlyList<IFileEntry> SelectedFiles { get; }
    public IVirtualFileSystem ActiveFileSystem { get; }
    public VfsPath ActivePath { get; }
    public IVirtualFileSystem OppositeFileSystem { get; }
    public VfsPath OppositePath { get; }

    /// <summary>Reloads whatever the active panel is showing now. Fire-and-forget by contract:
    /// <see cref="IToolContext"/> is synchronous and plugins are not expected to await anything.</summary>
    public void Refresh() => _ = _active.FileList.LoadAsync(_active.CurrentPath);
}
