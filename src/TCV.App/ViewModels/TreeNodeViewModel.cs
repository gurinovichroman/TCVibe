using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Collections;
using TCV.App.Navigation;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>
/// A node of a folder tree (the panel's Tree view, the docked tree). Children are read lazily, on
/// the first expansion, and kept up to date afterwards without rebuilding: a fresh listing is merged
/// into the existing nodes, so the subtrees the user opened stay open (see <see cref="ApplyListing"/>).
/// </summary>
public partial class TreeNodeViewModel : ObservableObject
{
    private readonly IVirtualFileSystem? _vfs;
    private readonly bool _foldersOnly;
    private Task? _loadingTask;

    /// <summary>The newest listing asked for; an older one that answers late is dropped.</summary>
    private int _listingGeneration;

    /// <summary>Set while code opens the node on the way to a path: that is not a request to re-read it.</summary>
    private bool _expandingFromCode;

    // Placeholder child so the expand arrow is visible before real children are loaded
    private static readonly TreeNodeViewModel Dummy = new("...", string.Empty, true, null);

    /// <param name="foldersOnly">The node and everything under it lists folders only (the docked tree).</param>
    public TreeNodeViewModel(string name, string fullPath, bool isDirectory, IVirtualFileSystem? vfs, bool foldersOnly = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        _vfs = vfs;
        _foldersOnly = foldersOnly;
        // Only directories can have children
        if (isDirectory && vfs != null)
        {
            Children.Add(Dummy);
        }
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

    /// <summary>A drive's root («C:\»): drawn with the drive icon instead of a folder.</summary>
    public bool IsDriveRoot =>
        IsDirectory && FullPath.Length > 0
        && string.Equals(Path.GetPathRoot(FullPath), FullPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The line icon of the node, by the same rule as a row of the list (<see cref="FileIconClass"/>).</summary>
    public FileIconKind IconKind => FileIconClass.For(Path.GetExtension(Name).TrimStart('.'), IsDirectory);

    /// <summary>The extension badge a file node shows instead of its line icon, or null.</summary>
    public string? TypeBadge => FileIconClass.BadgeFor(Path.GetExtension(Name).TrimStart('.'), IsDirectory);

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    /// <summary>True once the children are a real listing rather than the placeholder.</summary>
    public bool IsLoaded => _loadingTask is { IsCompleted: true };

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    private BitmapSource? _iconCache;
    private bool _iconLoaded;

    public BitmapSource? SmallIcon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _iconCache = IsDirectory
                    ? ShellIconService.GetSmallIcon(string.Empty, true)
                    : ShellIconService.GetSmallIcon(Path.GetExtension(Name), false);
            }
            return _iconCache;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || !IsDirectory || _expandingFromCode)
        {
            return;
        }

        // The first expansion reads the folder; opening it again re-reads it, as Explorer does, so a
        // folder created since then shows up without a manual refresh.
        if (_loadingTask is null)
        {
            _ = EnsureLoadedAsync();
        }
        else if (_loadingTask.IsCompleted)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>Ensures children are loaded. Safe to call concurrently — loading runs only once.</summary>
    public Task EnsureLoadedAsync()
    {
        if (!IsDirectory)
        {
            return Task.CompletedTask;
        }

        return _loadingTask ??= ReadAndApplyAsync(firstLoad: true);
    }

    /// <summary>
    /// Opens the node on the way to a path and waits for its children. Unlike a click on the arrow it
    /// does not re-read a folder that is already loaded — following the panel must not cost a listing
    /// per level on every navigation.
    /// </summary>
    public Task ExpandAsync()
    {
        _expandingFromCode = true;
        try
        {
            IsExpanded = true;
        }
        finally
        {
            _expandingFromCode = false;
        }

        return EnsureLoadedAsync();
    }

    /// <summary>Re-reads a loaded node's folder; a node never opened has nothing to bring up to date.</summary>
    public Task RefreshAsync() =>
        IsDirectory && _loadingTask is not null ? ReadAndApplyAsync(firstLoad: false) : Task.CompletedTask;

    /// <summary>
    /// Re-reads this node and every open node under it — what a manual refresh does. Collapsed
    /// subtrees are left alone: they are re-read when opened again.
    /// </summary>
    public async Task RefreshOpenSubtreeAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        await RefreshAsync();
        foreach (var child in Children.Where(c => c.IsExpanded && c.IsLoaded).ToList())
        {
            await child.RefreshOpenSubtreeAsync();
        }
    }

    /// <summary>
    /// Merges a listing of this folder that was read elsewhere — the panel just loaded it — into the
    /// children, without touching the disk again. Nodes that are still there are kept with their own
    /// subtrees, vanished ones go, new ones appear in order. A node never opened keeps its
    /// placeholder: it will read its own listing on the first expansion.
    /// </summary>
    public void ApplyListing(IReadOnlyList<IFileEntry> listing)
    {
        if (!IsDirectory || !IsLoaded)
        {
            return;
        }

        _listingGeneration++;   // a read still in flight is older than this
        Merge(listing);
    }

    private async Task ReadAndApplyAsync(bool firstLoad)
    {
        if (_vfs == null)
        {
            return;
        }

        var generation = ++_listingGeneration;
        IReadOnlyList<IFileEntry> entries;
        try
        {
            // The disk lister runs on the thread pool (INV-06); the merge comes back here, where the tree lives.
            entries = await _vfs.ListAsync(new VfsPath(FullPath));
        }
        catch
        {
            if (firstLoad && generation == _listingGeneration)
            {
                Children.Clear();
            }

            return;   // a folder that cannot be read now keeps what it showed
        }

        if (generation == _listingGeneration)
        {
            Merge(entries);
        }
    }

    private void Merge(IReadOnlyList<IFileEntry> listing)
    {
        var existing = new Dictionary<(string Name, bool IsDirectory), TreeNodeViewModel>();
        foreach (var child in Children)
        {
            if (!ReferenceEquals(child, Dummy))
            {
                existing.TryAdd((child.Name.ToUpperInvariant(), child.IsDirectory), child);
            }
        }

        var target = FolderTree.ChildrenOf(listing, _foldersOnly)
            .Select(e => existing.TryGetValue((e.Name.ToUpperInvariant(), e.IsDirectory), out var kept)
                ? kept
                : e.IsDirectory
                    ? new TreeNodeViewModel(e.Name, Path.Combine(FullPath, e.Name), true, _vfs, _foldersOnly)
                    : new TreeNodeViewModel(e.Name, Path.Combine(FullPath, e.Name), false, null))
            .ToList();

        ListReconciler.Reconcile(Children, target);
    }
}
