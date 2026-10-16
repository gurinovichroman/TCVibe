using CommunityToolkit.Mvvm.ComponentModel;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>How a listing ended: the folder, and the failure — null when it was read.</summary>
public sealed class ListingCompletedEventArgs(string path, Exception? error) : EventArgs
{
    public string Path { get; } = path;

    public Exception? Error { get; } = error;
}

/// <summary>
/// What the list needs for a connection that dropped and comes back by itself (6.82): the outcome of
/// each listing, for the shell to notice a drop; what the reconnect is doing, for the list and the
/// chip; and swapping a fresh connection in under the tab without leaving it.
/// </summary>
public partial class FileListViewModel
{
    /// <summary>
    /// Raised when a listing finished — read or failed, not cancelled — on the thread that loaded
    /// it (the UI thread in the application). The shell starts reconnecting on a dropped connection.
    /// </summary>
    public event EventHandler<ListingCompletedEventArgs>? ListingCompleted;

    /// <summary>
    /// What the automatic reconnect is doing ("next try in 5 s, attempt 2 of 8"), or null when none
    /// runs. Shown instead of the error in the list, and by the chip.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting))]
    [NotifyPropertyChangedFor(nameof(IsConnectionFailed))]
    private string? _reconnectStatus;

    /// <summary>The connection under the panel dropped and is being opened again by itself.</summary>
    public bool IsReconnecting => ReconnectStatus is not null;

    partial void OnReconnectStatusChanged(string? value) => UpdateEmptyState();

    private void RaiseListingCompleted(string path, Exception? error) =>
        ListingCompleted?.Invoke(this, new ListingCompletedEventArgs(path, error));

    /// <summary>
    /// Applies a listing through the dispatcher, as the load always has; without an application (the
    /// tests) right here, so a reconnect can be followed to the listing that proves it.
    /// </summary>
    private static Task OnDispatcherAsync(Action apply)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
        {
            return dispatcher.InvokeAsync(apply).Task;
        }

        apply();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Puts a fresh connection in place of the dropped one on top of the panel and reads
    /// <paramref name="path"/> on it. The tab stays where it was: the frames under the server (the
    /// disk folder it was opened from) are kept, and so is the connection's description. The old file
    /// system is closed once no operation streams from it any more (INV-09). False when the top mount
    /// is no server, which leaves <paramref name="vfs"/> to the caller.
    /// </summary>
    public bool ReplaceRemoteFileSystem(IVirtualFileSystem vfs, string path)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        if (!IsRemote || RemoteConnection is null)
        {
            return false;
        }

        DisposeActiveVfs();
        _activeVfs = vfs;
        NotifyMountChanged();

        var target = string.IsNullOrEmpty(path) ? "/" : path;
        var samePath = string.Equals(CurrentPath, target, StringComparison.Ordinal);
        CurrentPath = target;
        if (samePath)
        {
            _ = LoadAsync(target);
        }

        return true;
    }

    /// <summary>
    /// Keeps one file system of this panel — on screen or parked in a background tab — from being
    /// disposed while the handle is held: a keep-alive ping in flight, as <see cref="LeaseActiveVfs"/>
    /// does for an operation. Taken and released on the UI thread.
    /// </summary>
    public IDisposable LeaseFileSystem(IVirtualFileSystem vfs)
    {
        ArgumentNullException.ThrowIfNull(vfs);
        var leased = ReferenceEquals(vfs, _vfs) ? new List<IVirtualFileSystem>() : [vfs];
        foreach (var held in leased)
        {
            _vfsLeases[held] = _vfsLeases.GetValueOrDefault(held) + 1;
        }

        return new VfsLease(this, leased);
    }

    /// <summary>The file systems mounted on screen, top first; the disk under them is not one of them.</summary>
    public IReadOnlyList<IVirtualFileSystem> MountedFileSystems()
    {
        var mounted = new List<IVirtualFileSystem>();
        if (!ReferenceEquals(_activeVfs, _vfs))
        {
            mounted.Add(_activeVfs);
        }

        mounted.AddRange(_navStack.Select(f => f.Vfs).Where(v => !ReferenceEquals(v, _vfs)));
        return mounted;
    }

    /// <summary>The file systems a background tab keeps open (ADR-0032), top first.</summary>
    public IReadOnlyList<IVirtualFileSystem> MountedFileSystems(ParkedMounts parked)
    {
        ArgumentNullException.ThrowIfNull(parked);
        var mounted = new List<IVirtualFileSystem> { parked.Top };
        mounted.AddRange(parked.Frames.Reverse().Select(f => f.Vfs).Where(v => !ReferenceEquals(v, _vfs) && !mounted.Contains(v)));
        return mounted;
    }
}
