using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Commands;
using TCV.App.Localization;
using TCV.App.Navigation;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
using TCV.App.FileSystem;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>
/// The navigation half of <see cref="MainViewModel"/>: the instant name index and the docked
/// folder tree (6.21). Split out for the reason given in
/// [TRAP-17](../../../CLAUDE.md).
/// </summary>
public partial class MainViewModel
{
    // ── Instant name search (6.21) ────────────────────────────────────────────

    /// <summary>
    /// Ctrl+Shift+P-style instant search over the whole indexed disk, as opposed to Ctrl+P which
    /// indexes the current project on demand. Opens immediately on whatever the index holds; the
    /// cache is read off the UI thread on first use, and a missing or stale one is rebuilt behind
    /// the open window, which refreshes itself as entries arrive.
    /// </summary>
    [RelayCommand]
    private void OpenNameSearch()
    {
        var lang = LocalizationManager.CurrentLanguage;
        var index = _nameIndex.Index;

        var vm = new CommandPaletteViewModel(
            query => index.Query(query, limit: 300)
                .Select(e => new CommandPaletteItem(
                    e.Path,
                    e.IsDirectory ? e.Name + "/" : e.Name,
                    e.Directory,
                    string.Empty))
                .ToList(),
            lang,
            LocalizationManager.Get("NameIndex_Title"),
            LocalizationManager.Get("NameIndex_Placeholder"))
        {
            Status = DescribeIndex(),
        };

        // Loading the cache, progress and a finished rebuild all land here from worker threads; the
        // window refreshes so the first use on a fresh copy fills in instead of staying empty. While
        // a rebuild runs, at most once a second: each refresh re-queries the whole index on the UI
        // thread, and progress arrives every couple of thousand entries.
        var lastRefresh = DateTime.MinValue;
        void OnChanged(object? sender, EventArgs e) =>
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                vm.Status = DescribeIndex();
                var now = DateTime.UtcNow;
                if (_nameIndex.IsBuilding && now - lastRefresh < TimeSpan.FromSeconds(1))
                {
                    return;
                }

                lastRefresh = now;
                vm.Refresh();
            });

        _nameIndex.Changed += OnChanged;
        try
        {
            _nameIndex.SetPriorityFolders(NameIndexPriorityFolders());
            _ = _nameIndex.EnsureReadyAsync();

            var dlg = new CommandPaletteDialog(vm) { Owner = Application.Current.MainWindow };
            if (dlg.ShowDialog() == true && vm.SelectedItem is { } chosen)
                _ = RevealIndexedPathAsync(chosen.Id);
        }
        finally
        {
            _nameIndex.Changed -= OnChanged;
        }
    }

    /// <summary>
    /// Ctrl+K Ctrl+R — rescan the indexed roots in the background. While a rebuild is already running
    /// the same command offers to cancel it, so a whole-disk walk is never a trap.
    /// </summary>
    [RelayCommand]
    private async Task RebuildNameIndex()
    {
        if (_nameIndex.IsBuilding)
        {
            var cancel = await _dialogs.ConfirmAsync(
                LocalizationManager.Get("NameIndex_Title"),
                string.Format(LocalizationManager.Get("NameIndex_CancelPrompt"), _nameIndex.BuildProgress));
            if (cancel)
            {
                _nameIndex.CancelRebuild();
            }
            return;
        }

        ShowNotice(LocalizationManager.Get("NameIndex_Started"));
        _nameIndex.SetPriorityFolders(NameIndexPriorityFolders());
        var roots = await NameIndexService.DefaultRootsAsync();
        var completed = await _nameIndex.RebuildAsync(roots);
        ShowNotice(
            completed
                ? string.Format(LocalizationManager.Get("NameIndex_Rebuilt"), _nameIndex.Index.Count)
                : LocalizationManager.Get("NameIndex_Cancelled"),
            TimeSpan.FromSeconds(8));
    }

    /// <summary>
    /// The folders the user works in, which a whole-disk index rebuild walks first (6.56): every
    /// workspace's tabs, the bookmarks and the disk folders both panels stand in. Read on the UI
    /// thread, where these collections live; the index service keeps a copy.
    /// </summary>
    private IEnumerable<string> NameIndexPriorityFolders() =>
        Workspaces.Items
            .SelectMany(w => w.Left.Tabs.Concat(w.Right.Tabs))
            .Select(t => t.Path)
            .Concat(Bookmarks.Select(b => b.Path))
            .Concat([LeftPanel.FileList.RootLocalPath, RightPanel.FileList.RootLocalPath])
            .ToList();

    private string DescribeIndex()
    {
        if (_nameIndex.IsBuilding)
        {
            return _nameIndex.BuildProgress > 0
                ? string.Format(LocalizationManager.Get("NameIndex_BuildingCount"), _nameIndex.BuildProgress)
                : LocalizationManager.Get("NameIndex_Building");
        }

        var count = _nameIndex.Index.Count;
        if (count == 0)
        {
            return LocalizationManager.Get("NameIndex_Empty");
        }

        return string.Format(
            LocalizationManager.Get(_nameIndex.Index.IsTruncated ? "NameIndex_CountTruncated" : "NameIndex_Count"),
            count);
    }

    /// <summary>
    /// Navigates the active panel to an indexed path: into a directory, or to the directory of a
    /// file with that file selected.
    /// </summary>
    private async Task RevealIndexedPathAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // The index is a snapshot: the entry may have been deleted or renamed since. Say so and drop
        // it, instead of navigating the panel into a folder that is not there. Checked before the
        // panel leaves any mount — bailing out after that would leave it unmounted over the
        // archive's stale rows — and off the UI thread, since the path may be on a share (INV-06).
        var (isDirectory, isFile) = await Task.Run(() => (Directory.Exists(path), File.Exists(path)));
        if (!isDirectory && !isFile)
        {
            _nameIndex.NoteChanged(Path.GetDirectoryName(path) ?? path);
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("NameIndex_Title"),
                string.Format(LocalizationManager.Get("NameIndex_Missing"), path));
            return;
        }

        // Index entries are real Windows paths, not VFS paths (unlike Ctrl+P — TRAP-18), so they
        // need no normalisation; NavigateActiveToDisk leaves an open archive first.
        if (isDirectory)
        {
            NavigateActiveToDisk(path);
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return;
        NavigateActiveToDisk(directory, Path.GetFileName(path));
    }

    // ── Docked folder tree (6.21) ─────────────────────────────────────────────

    /// <summary>
    /// Whether the folder tree is docked to the left of the panels. Unlike the Tree *view mode*,
    /// which replaces a panel's file list, this one stays visible while both panels show files —
    /// the arrangement every IDE uses.
    /// </summary>
    [ObservableProperty]
    private bool _showFolderTree;

    /// <summary>
    /// Roots of the docked tree: one node per ready drive. Folders only — the panels next to it
    /// already list the files, and a tree full of files is too long to find a folder in.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<TreeNodeViewModel> FolderTreeRoots { get; } = new();

    /// <summary>
    /// The node the tree selected itself to follow the active panel. Its selection event is not a pick
    /// by the user and must not navigate: by the time a freshly opened level shows it, the panel may
    /// have moved on — into an archive, which navigating would close (TRAP-55).
    /// </summary>
    private TreeNodeViewModel? _folderTreeFollowed;

    /// <summary>The node the last walk to the active panel's folder ended on.</summary>
    private TreeNodeViewModel? _folderTreeFollowedLast;

    /// <summary>The newest walk to the active panel's folder; an older one still loading gives way.</summary>
    private int _folderTreeFollowGeneration;

    [RelayCommand]
    private async Task ToggleFolderTree()
    {
        ShowFolderTree = !ShowFolderTree;
        if (!ShowFolderTree)
        {
            return;
        }

        // Hidden, the tree heard about the panels' listings but not about anything else: bring it
        // up to date before it is seen again.
        if (FolderTreeRoots.Count == 0)
        {
            await RefreshFolderTreeRootsAsync();
        }
        else
        {
            await RefreshFolderTreeAsync();
        }

        await FollowActivePanelInFolderTreeAsync();
    }

    /// <summary>
    /// Wires the docked tree to the panels (6.52): it follows the active panel's folder, takes every
    /// disk listing the panels make, and its drive roots follow the drive list. Called once, from the
    /// constructor, when both panels exist.
    /// </summary>
    private void WireFolderTree()
    {
        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            var source = panel;
            source.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PanelViewModel.CurrentPath) && ReferenceEquals(source, ActivePanel))
                {
                    _ = FollowActivePanelInFolderTreeAsync();
                }
            };
            source.FileList.DiskListingLoaded += (_, e) => OnPanelListedDiskFolder(source, e);
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivePanel))
            {
                _ = FollowActivePanelInFolderTreeAsync();
            }
        };

        // Both drive bars read the same drives; one of them is enough to hear that they changed.
        LeftPanel.DriveBar.DrivesRefreshed += (_, _) => _ = RefreshFolderTreeRootsAsync();
    }

    /// <summary>
    /// A panel listed a disk folder: the docked tree and the other panel's Tree view show that folder
    /// too, so their node takes the listing (the panel's own Tree view has already taken it).
    /// </summary>
    private void OnPanelListedDiskFolder(PanelViewModel source, DiskListingEventArgs e)
    {
        var other = ReferenceEquals(source, LeftPanel) ? RightPanel : LeftPanel;
        other.FileList.ApplyListingToTree(e.Path, e.Entries);

        if (FolderTreeRoots.Count > 0)
        {
            FolderTreeNodes.FindLoaded(FolderTreeRoots, e.Path)?.ApplyListing(e.Entries);
        }
    }

    private TreeNodeViewModel CreateFolderTreeRoot(string name, string root) =>
        new(name, root, true, _fs, foldersOnly: true);

    /// <summary>
    /// Reads the drives (off the UI thread, INV-06 — <c>IsReady</c> blocks on a sleeping network
    /// drive) and merges them into the roots: drives that stay keep what is open under them.
    /// </summary>
    private async Task RefreshFolderTreeRootsAsync()
    {
        // Built on first show; until then there is nothing to keep current.
        if (FolderTreeRoots.Count == 0 && !ShowFolderTree)
        {
            return;
        }

        var drives = await FolderTreeNodes.ReadDriveRootsAsync();
        if (drives is null)
        {
            _logger.LogWarning("Could not enumerate drives for the folder tree");
            return;
        }

        FolderTreeNodes.MergeRoots(FolderTreeRoots, drives, CreateFolderTreeRoot);
    }

    /// <summary>
    /// Re-reads the drives and every open folder of the docked tree. Folders no panel shows are not
    /// watched, so this — the refresh command, or showing the tree again — is how it catches up.
    /// </summary>
    public async Task RefreshFolderTreeAsync()
    {
        if (FolderTreeRoots.Count == 0)
        {
            return;
        }

        await RefreshFolderTreeRootsAsync();
        foreach (var root in FolderTreeRoots.ToList())
        {
            await root.RefreshOpenSubtreeAsync();
        }
    }

    /// <summary>
    /// Opens the docked tree down to the active panel's folder and selects it, like an IDE's project
    /// tree tracking the open file. A mounted panel (archive, server) is not followed: the tree shows
    /// the disk, and its last disk folder stays selected.
    /// </summary>
    private async Task FollowActivePanelInFolderTreeAsync()
    {
        if (!ShowFolderTree || FolderTreeRoots.Count == 0)
        {
            return;
        }

        var generation = ++_folderTreeFollowGeneration;
        var panel = ActivePanel;
        if (panel.FileList.IsMounted)
        {
            return;
        }

        // Only the way to the folder is opened, not the folder itself: that would read it a second
        // time on every navigation, while the panel is reading it anyway.
        var node = await FolderTreeNodes.ExpandToAsync(FolderTreeRoots, panel.CurrentPath, expandTarget: false);
        if (generation != _folderTreeFollowGeneration)
        {
            return;
        }

        if (node is null)
        {
            // A network share, or a drive the tree does not list: no node is where the panel is, and
            // leaving the last one highlighted would say otherwise.
            if (_folderTreeFollowedLast is { IsSelected: true } stale)
            {
                stale.IsSelected = false;
            }

            _folderTreeFollowedLast = null;
            return;
        }

        _folderTreeFollowedLast = node;
        if (node.IsSelected)
        {
            return;   // picked in the tree a moment ago: that is where the panel went
        }

        _folderTreeFollowed = node;
        node.IsSelected = true;
    }

    /// <summary>
    /// The docked tree's selection moved. A move the tree made itself to follow the panel is not a
    /// pick; anything else — a click, the arrow keys — navigates the active panel there.
    /// </summary>
    public void OnFolderTreeNodeSelected(TreeNodeViewModel node)
    {
        if (ReferenceEquals(node, _folderTreeFollowed))
        {
            _folderTreeFollowed = null;
            return;
        }

        NavigateToTreeNode(node);
    }

    /// <summary>Navigates the active panel to a node the user picked in the docked tree.</summary>
    public void NavigateToTreeNode(TreeNodeViewModel node)
    {
        if (node is null || string.IsNullOrEmpty(node.FullPath))
        {
            return;
        }

        _ = RevealIndexedPathAsync(node.FullPath);
    }

    // ─────────────────────────── Fuzzy file finder (Ctrl+P) ───────────────────────────

    /// <summary>Portable store behind the picker's habit ranking (frequency + recency).</summary>
    private readonly Navigation.FileUsageStore _fileUsage = new();

    // Ctrl+P — fuzzy file finder over the project
    [RelayCommand]
    private async Task OpenFileFinder()
    {
        var fileList = ActivePanel.FileList;

        // Walking a remote tree recursively means thousands of round trips before the first
        // keystroke pays off. An archive is fine: its listing comes from an open local index.
        if (fileList.IsRemote)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Finder_Title"),
                LocalizationManager.Get("Finder_RemoteUnsupported"));
            return;
        }

        var vfs = fileList.ActiveVfs;

        // "Find file" means "in this project": index from the repository root when there is one,
        // so Ctrl+P finds a sibling module without navigating up first. Inside a mount the panel
        // path *is* the root — there is nothing above it to climb to.
        var root = fileList.IsMounted
            ? fileList.CurrentPath
            : await Navigation.ProjectRootLocator.FindAsync(vfs, fileList.CurrentPath);

        var options = new Navigation.FileIndexOptions
        {
            RespectGitignore = !fileList.IsMounted,      // .gitignore is a working-copy concept
            IncludeHidden = fileList.ShowHiddenFiles,    // follow what the panel itself shows
        };

        var vm = new FileFinderViewModel(root, _fileUsage.LoadIndex(DateTime.UtcNow), FinderStrings());
        var dlg = new FileFinderDialog(vm, vfs, new VfsPath(root), options)
        {
            Owner = Application.Current.MainWindow,
        };

        if (dlg.ShowDialog() != true || vm.SelectedItem is not { } chosen) return;

        _fileUsage.Record(chosen.FullPath, DateTime.UtcNow);
        RevealIndexEntry(chosen);
    }

    private static FileFinderStrings FinderStrings() => new(
        LocalizationManager.Get("Finder_Title"),
        LocalizationManager.Get("Finder_Placeholder"),
        LocalizationManager.Get("Finder_Indexing"),
        LocalizationManager.Get("Finder_Count"),
        LocalizationManager.Get("Finder_Truncated"),
        LocalizationManager.Get("Finder_Scope"));

    /// <summary>
    /// Jumps the active panel to a picker hit: into the directory, or to the file's folder with the
    /// file selected. Already in that folder — select straight away, since without a reload the
    /// pending-selection hand-off would never be consumed.
    /// </summary>
    private void RevealIndexEntry(Navigation.FileIndexEntry entry)
    {
        var fileList = ActivePanel.FileList;

        // Index paths are VFS paths (forward slashes). On disk the panel shows Windows form, so
        // normalise — otherwise the breadcrumb and the tab would disagree with every other path.
        string ToPanelPath(string vfsPath) => fileList.IsMounted
            ? vfsPath
            : WindowsFileSystem.ToWindowsPath(new VfsPath(vfsPath));

        if (entry.IsDirectory)
        {
            ActivePanel.CurrentPath = ToPanelPath(entry.FullPath);
            return;
        }

        // Not Path.GetDirectoryName: it rewrites "/dir/file.txt" as "\dir", which is exactly the
        // Windows form the mounted branch above must not receive (TRAP-18). VfsPath.Parent
        // normalises back to slashes, and ToPanelPath converts for a panel on disk.
        var dir = new VfsPath(entry.FullPath).Parent;
        if (dir is null) return;

        var target = ToPanelPath(dir.Value);
        if (string.Equals(target, fileList.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            fileList.SelectedItem = fileList.Files.FirstOrDefault(
                f => string.Equals(f.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
            return;
        }

        fileList.SetPendingSelectName(entry.Name);   // consumed once the listing arrives
        ActivePanel.CurrentPath = target;
    }

    // Ctrl+F — Search
    [RelayCommand]
    private void OpenSearch()
    {
        // A server or an archive is searched where the panel stands, through its own file system;
        // the disk folder underneath the mount is not what the user is looking at (6.55).
        var fileList = ActivePanel.FileList;
        // Only a list of disk hits stands for the disk folder under it. Hits of a search on a server
        // or in an archive are searched again on that mount (TRAP-28), not on the disk beneath it.
        var start = SearchStart.For(
            fileList.IsMounted, fileList.IsSearchResults && fileList.EntriesOnDisk, fileList.CurrentPath, fileList.RootLocalPath);

        if (start.Scope == SearchScope.Mount)
        {
            OpenMountSearch(fileList, start.Path);
            return;
        }

        var vm = new SearchDialogViewModel(_fs, start.Path, _archivePlugin, _searchPresets);
        var dlg = new SearchDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.NavigateRequested += (_, result) => _ = NavigateToSearchResultAsync(result);
        vm.ActionRequested += (_, request) => _ = ApplySearchResultActionAsync(request, vm);
        dlg.Show(); // non-blocking
    }

    /// <summary>
    /// Opens the search on the panel's mount. The window is non-modal and the walk reads through the
    /// connection, so the mount is leased for as long as the window is open: leaving the server
    /// mid-search must not close the connection under it (INV-09). The lease is taken and released on
    /// the UI thread.
    /// </summary>
    private void OpenMountSearch(FileListViewModel fileList, string startPath)
    {
        // Over a results list the mount under it is searched: the list is replaced by the new hits,
        // never stacked on (ADR-0028). The lease covers the mounts beneath the list too.
        var vfs = fileList.ActiveVfs is SearchResultsFileSystem results ? results.Inner : fileList.ActiveVfs;
        var lease = fileList.LeaseActiveVfs();

        var vm = new SearchDialogViewModel(vfs, startPath, archivePlugin: null, _searchPresets) { SearchesMount = true };
        var dlg = new SearchDialog(vm) { Owner = Application.Current.MainWindow };
        dlg.NavigateRequested += (_, result) => RevealMountSearchHit(vfs, result, vm);
        vm.ActionRequested += (_, request) => _ = ApplyMountSearchActionAsync(vfs, request, vm);
        dlg.Closed += (_, _) => lease.Dispose();
        dlg.Show();
    }

    /// <summary>
    /// The panel that still has <paramref name="vfs"/> open, the active one first, made active, with a
    /// search-results list shown over it taken away (it is replaced, never stacked). Null — and the
    /// search window says why — when no panel shows the mount any more: a hit's path means nothing on
    /// disk or in another mount.
    /// </summary>
    private PanelViewModel? ActivatePanelShowing(IVirtualFileSystem vfs, SearchDialogViewModel dialog) =>
        ActivatePanelShowing(vfs, dialog, out _);

    /// <param name="leftResults">True when a results list was taken away: the listing is being re-read.</param>
    private PanelViewModel? ActivatePanelShowing(IVirtualFileSystem vfs, SearchDialogViewModel dialog, out bool leftResults)
    {
        leftResults = false;
        static bool Shows(FileListViewModel list, IVirtualFileSystem vfs) =>
            ReferenceEquals(list.ActiveVfs, vfs)
            || list.ActiveVfs is SearchResultsFileSystem results && ReferenceEquals(results.Inner, vfs);

        var panel = new[] { ActivePanel, OppositePanel }.FirstOrDefault(p => Shows(p.FileList, vfs));
        if (panel is null)
        {
            dialog.StatusText = string.Format(LocalizationManager.Get("Search_MountGone"), vfs.DisplayName);
            return null;
        }

        if (!ReferenceEquals(panel, ActivePanel))
        {
            ActivatePanel(left: ReferenceEquals(panel, LeftPanel));
        }

        // Back to the mount itself. The view does not own the mount under it, so this closes nothing.
        if (panel.FileList.ActiveVfs is SearchResultsFileSystem)
        {
            panel.FileList.Unmount();
            leftResults = true;
        }

        return panel;
    }

    /// <summary>
    /// Copy, move, delete and "show in panel" over the hits of a search on a server or in an archive
    /// (6.60). As with a disk search, the hits are first listed in the panel — over the mount they
    /// came from, so their paths keep meaning that mount — and the ordinary commands act on them all:
    /// a server's files through its file system, an archive's entries through the archive engine.
    /// </summary>
    private async Task ApplyMountSearchActionAsync(
        IVirtualFileSystem vfs, SearchResultActionRequest request, SearchDialogViewModel dialog)
    {
        if (request.Results.Count == 0) return;
        if (ActivatePanelShowing(vfs, dialog) is not { } panel) return;

        var fileList = panel.FileList;
        var view = new SearchResultsFileSystem(vfs, request.Results.Select(r => r.Entry).ToList());
        fileList.Mount(view, SearchResultsFileSystem.RootPath, kind: MountKind.SearchResults);
        await fileList.LoadAsync(SearchResultsFileSystem.RootPath);

        await ActOnListedHitsAsync(fileList, request, dialog);
    }

    /// <summary>
    /// Runs a search window's action on the hits just listed in <paramref name="fileList"/> (the active
    /// panel), as one selection, and drops from the window the hits a move or a delete took away.
    /// </summary>
    private async Task ActOnListedHitsAsync(
        FileListViewModel fileList, SearchResultActionRequest request, SearchDialogViewModel dialog)
    {
        // The listing holds exactly the hits; selecting them all makes the ordinary commands act on the
        // whole hit set. Deselect first: only the hits are to take part, never whatever was selected before.
        fileList.DeselectAll();
        fileList.SelectAll();

        switch (request.Action)
        {
            case SearchResultAction.Copy: await Files.CopySelectionAsync(); break;
            case SearchResultAction.Move: await Files.MoveSelectionAsync(); break;
            case SearchResultAction.Delete: await Files.DeleteSelectionAsync(); break;
            case SearchResultAction.FeedToPanel: break;   // the listing was the whole request
        }

        // Files that were moved or deleted must not stay in the result list: the next click would
        // act on something that is no longer there.
        if (request.Action is SearchResultAction.Move or SearchResultAction.Delete)
        {
            await dialog.PruneMissingAsync();
        }
    }

    /// <summary>
    /// Shows a hit of a mount search in the panel that still has that mount open, the active one first.
    /// The hit's path means nothing on disk or in another mount, so when no panel shows the mount any
    /// more nothing is navigated and the search window says why.
    /// </summary>
    private void RevealMountSearchHit(IVirtualFileSystem vfs, SearchResultViewModel hit, SearchDialogViewModel dialog)
    {
        if (ActivatePanelShowing(vfs, dialog, out var leftResults) is not { } panel) return;

        // VFS path inside the mount: its parent through VfsPath, never Path.GetDirectoryName (TRAP-18).
        var fileList = panel.FileList;
        var folder = new VfsPath(hit.FullPath).Parent?.Value ?? "/";
        // Ordinal: a server's names are case-sensitive, and "/Pub" is not "/pub" there.
        if (string.Equals(folder, fileList.CurrentPath, StringComparison.Ordinal))
        {
            if (leftResults)
            {
                // The rows on screen are still the results list's: select once the folder is re-read.
                fileList.SetPendingSelectName(hit.Name);
                return;
            }

            fileList.SelectedItem = fileList.Files.FirstOrDefault(
                f => string.Equals(f.Name, hit.Name, StringComparison.Ordinal));
            return;
        }

        fileList.SetPendingSelectName(hit.Name);   // consumed once the listing arrives
        fileList.CurrentPath = folder;
    }

    /// <summary>
    /// Runs an operation the user asked for over a set of search hits (6.17). Everything that
    /// mutates goes through the same commands as the panel — the operation queue and its log
    /// (INV-10) — by feeding the hits into the active panel first, so a copy started from the
    /// search window is undoable and logged exactly like one started with F5.
    /// </summary>
    private async Task ApplySearchResultActionAsync(SearchResultActionRequest request, SearchDialogViewModel dialog)
    {
        if (request.Results.Count == 0) return;

        var entries = request.Results.Select(r => r.Entry).ToList();
        await FeedSearchResultsToPanelAsync(entries);
        await ActOnListedHitsAsync(ActivePanel.FileList, request, dialog);
    }

    /// <summary>
    /// Mounts the hits as a synthetic directory in the active panel, so they can be browsed and
    /// operated on together even though they live in different folders. Returns once the listing
    /// is actually on screen.
    /// </summary>
    private async Task FeedSearchResultsToPanelAsync(IReadOnlyList<IFileEntry> entries)
    {
        var fileList = ActivePanel.FileList;

        // The hits are disk files, so the synthetic view wraps the disk file system, never an open
        // archive or connection (their entries would be looked up in the wrong place). Popping every
        // mount also keeps a second feed from stacking on the first, where "up" would climb through
        // a chain of stale result sets.
        fileList.ReturnToDisk();

        var vfs = new SearchResultsFileSystem(fileList.ActiveVfs, entries);
        fileList.Mount(vfs, SearchResultsFileSystem.RootPath, kind: MountKind.SearchResults);

        // Mount only *starts* a load, and does not start one at all when the path is unchanged —
        // a caller that goes on to select and copy would otherwise act on the previous listing.
        // This load supersedes the one Mount kicked off; LoadAsync cancels its predecessor.
        await fileList.LoadAsync(SearchResultsFileSystem.RootPath);
    }

    /// <summary>Jumps the active panel to a search hit — on disk, or inside the archive it came from.</summary>
    private async Task NavigateToSearchResultAsync(SearchResultViewModel result)
    {
        if (result.ArchivePath is not null)
        {
            await NavigateToArchiveHitAsync(result);
            return;
        }

        var dir = Path.GetDirectoryName(result.FullPath);
        if (dir is null) return;

        // The listing arrives later, so the hit is selected when it does (pending selection), not
        // against the folder still on screen; and a hit on disk leaves any open mount first.
        NavigateActiveToDisk(dir, result.Name);
    }

    private async Task NavigateToArchiveHitAsync(SearchResultViewModel result)
    {
        // Same open-with-password path as double-clicking the archive itself
        if (!await Files.TryOpenArchiveInPanelAsync(result.ArchivePath!)) return;

        // Descend to the directory holding the hit; each assignment starts a load
        var entryPath = ArchiveFileSystem.NormalizePath(result.EntryPath);
        var lastSlash = entryPath.LastIndexOf('/');
        if (lastSlash > 0)
            ActivePanel.FileList.CurrentPath = "/" + entryPath[..lastSlash];

        // Consumed by FileListControl once that load completes
        ActivePanel.FileList.SetPendingSelectName(result.Name);
    }

    // ── Navigation shortcuts (5.17) ───────────────────────────────────────────

    // Ctrl+\ — jump to the root: the drive root on disk, or the mount root inside an archive/FTP.
    [RelayCommand]
    private void GoToRoot()
    {
        var fl = ActivePanel.FileList;
        if (fl.IsMounted)
        {
            fl.CurrentPath = "/";
            return;
        }

        var root = Path.GetPathRoot(ActivePanel.CurrentPath);
        if (!string.IsNullOrEmpty(root))
            ActivePanel.CurrentPath = root;
    }

    // Ctrl+Home — jump to the user's home folder (leaving any mount first).
    [RelayCommand]
    private void GoHome() =>
        NavigateActiveToDisk(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Ctrl+L — type a path for the active panel, as in Explorer and the browsers: the path bar turns
    /// into a text box with the current path selected. With the path bar hidden (Settings → Interface)
    /// the same is asked in a box, so the command still does what it says.
    /// </summary>
    [RelayCommand]
    private async Task EditPath()
    {
        var fileList = ActivePanel.FileList;
        if (_settings.Layout.ShowPathBar)
        {
            fileList.BeginEditPath();
            return;
        }

        var typed = await _dialogs.ShowInputAsync(
            LocalizationManager.Get("Nav_EditPathTitle"),
            LocalizationManager.Get("Nav_EditPathPrompt"),
            fileList.CurrentPath);
        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        bool found;
        try
        {
            found = await fileList.TryNavigateToTypedPathAsync(typed);
        }
        catch (Exception)
        {
            found = false;   // a dropped connection reads as "not there", as in the path bar
        }

        if (!found)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Breadcrumb_NotFoundTitle"),
                string.Format(LocalizationManager.Get("Breadcrumb_NotFound"), typed));
        }
    }

    // Navigate the active panel to a path pasted on the clipboard (folder → open it; file → open
    // its folder and highlight it).
    [RelayCommand]
    private async Task GoToClipboardPath()
    {
        var text = FileCommands.TryGetClipboardText()?.Trim().Trim('"');
        if (string.IsNullOrEmpty(text))
        {
            ShowNotice(LocalizationManager.Get("Nav_ClipboardNoPath"), TimeSpan.FromSeconds(4));
            return;
        }

        // Off the UI thread: the pasted path may be on a share that takes seconds to answer (INV-06).
        var (isDirectory, isFile) = await Task.Run(() => (Directory.Exists(text), File.Exists(text)));
        if (isDirectory)
            NavigateActiveToDisk(text);
        else if (isFile)
            NavigateActiveToDisk(Path.GetDirectoryName(text)!, Path.GetFileName(text));
        else
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Menu_GoClipboard"),
                string.Format(LocalizationManager.Get("Nav_ClipboardPathMissing"), text));
    }

    // Reveal the current folder (or the focused item) in Windows Explorer. Search hits are disk
    // files and show like any other (TRAP-28); on their list, which is no folder, "the folder" is
    // the one the hits were listed over.
    [RelayCommand]
    private async Task OpenInExplorer()
    {
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Menu_OpenExplorer")))
        {
            return;
        }

        var folder = ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath;
        var selected = GetSelectedOrCurrent();
        try
        {
            if (selected.Count == 1)
            {
                var winPath = FileSystem.WindowsFileSystem.ToWindowsPath(selected[0].Path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"/select,\"{winPath}\"") { UseShellExecute = true });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Menu_OpenExplorer"), ex.Message);
        }
    }

    /// <summary>Navigates the active panel to a real disk path, popping any open mount first.</summary>
    internal void NavigateActiveToDisk(string path, string? selectName = null)
    {
        if (string.IsNullOrEmpty(path)) return;

        var fl = ActivePanel.FileList;

        // Already showing that folder on disk: no load will happen, so a pending selection would
        // wait for some later, unrelated one (TRAP-18). Select straight away instead.
        if (!fl.IsMounted && string.Equals(
                path.TrimEnd('\\', '/'), ActivePanel.CurrentPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            if (selectName is not null
                && fl.Files.FirstOrDefault(f => string.Equals(f.Name, selectName, StringComparison.OrdinalIgnoreCase)) is { } row)
            {
                fl.SelectedItem = row;
            }
            return;
        }

        fl.UnmountAll();   // pop every frame at once — no throwaway reload per unmount

        if (selectName is not null)
            fl.SetPendingSelectName(selectName);
        ActivePanel.CurrentPath = path;
    }


    // Tab — switch active panel
}
