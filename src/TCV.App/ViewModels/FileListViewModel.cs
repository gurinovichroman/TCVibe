using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Collections;
using TCV.App.Columns;
using TCV.Git;
using TCV.App.Navigation;
using TCV.App.Settings;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

public partial class FileListViewModel : ObservableObject
{
    private readonly IVirtualFileSystem _vfs;         // base (local) filesystem
    private IVirtualFileSystem _activeVfs;            // current (may be archive)
    private readonly ILogger<FileListViewModel> _logger;
    private CancellationTokenSource _loadCts = new();
    private int _loadsStarted;
    private int _loadsApplied;
    private IReadOnlyList<IFileEntry> _allEntries = Array.Empty<IFileEntry>();
    private readonly Dictionary<string, FileEntryViewModel> _vmCache = new(StringComparer.OrdinalIgnoreCase);
    private FileEntryViewModel? _parentVm;
    private bool _suppressApplySort;
    private bool _suppressViewStateReactions;

    // One frame per mount, holding the state that mount replaced — popped by Unmount.
    private readonly Stack<MountFrame> _navStack = new();
    private MountKind _mountKind;
    internal readonly record struct MountFrame(
        IVirtualFileSystem Vfs, string Path, MountKind Kind, TCV.Network.ConnectionInfo? Connection);

    // Mounted file systems still being streamed from by a running operation, counted per file
    // system, plus the ones whose Dispose is waiting for those operations to let go. Per file system,
    // not per panel: a long-lived lease (a remote-edit session) must not pin every later mount.
    private readonly Dictionary<IVirtualFileSystem, int> _vfsLeases = new(ReferenceEqualityComparer.Instance);
    private readonly List<IVirtualFileSystem> _disposeWhenIdle = new();

    // Set before navigating up so the control can restore selection to the dir we left
    private string? _pendingSelectName;

    /// <summary>Consumes and returns the name of the item to select after the next directory load, or null.</summary>
    public string? ConsumePendingSelectName()
    {
        var name = _pendingSelectName;
        _pendingSelectName = null;
        return name;
    }

    /// <summary>Schedules a file name to be selected after the next directory load.</summary>
    public void SetPendingSelectName(string name) => _pendingSelectName = name;

    // ──────────────────────────────── View state preservation ────────────────────────────────

    /// <summary>Snapshot of scroll/selection state to restore after a refresh-triggered reload.</summary>
    public record ViewState(
        string? FocusedItemName,
        int FocusedItemIndex,
        IReadOnlyList<string> SelectedItemNames);

    private ViewState? _pendingViewState;

    // The listing on screen — the file system and folder of the last one that landed, null while the
    // list shows an error — and the listing the pending view state was saved from. A state belongs to
    // its rows only: a folder opened while the watcher's reload was on its way opened on the old cursor
    // row and the old scroll instead of at the top.
    private IVirtualFileSystem? _listedVfs;
    private string? _listedPath;
    private (IVirtualFileSystem? Vfs, string? Path) _pendingViewStateListing;

    /// <summary>Raised synchronously on the UI thread immediately before a directory load begins.</summary>
    public event EventHandler? BeforeDirectoryLoad;

    /// <summary>
    /// Saves current selection so it can be restored after the next LoadAsync.
    /// Call on the UI thread before triggering a refresh-reload.
    /// </summary>
    public void PrepareForRefresh()
    {
        // No cursor to save. Either the last load took away the row it stood on and a state still waits
        // to be restored — kept, instead of saving "the first row" — or a folder has just opened and the
        // view has yet to place its cursor: a state saved now would put it on the first row at the
        // scroll of the folder before.
        if (SelectedItem is null && (_pendingViewState is not null || SelectedFiles.Count == 0))
        {
            return;
        }

        _pendingViewState = new ViewState(
            FocusedItemName: SelectedItem?.Name,
            FocusedItemIndex: SelectedItem is { } s ? Files.IndexOf(s) : 0,
            SelectedItemNames: SelectedFiles.Select(f => f.Name).ToList()
        );
        _pendingViewStateListing = (_listedVfs, _listedPath);
    }

    /// <summary>
    /// Whether the pending view state was saved from the rows of <paramref name="path"/> on
    /// <paramref name="vfs"/>: also a refresh asked for after the panel had moved on but before the new
    /// folder came in describes the old rows. Disk paths compare as Windows does, ignoring case.
    /// </summary>
    private bool PendingViewStateDescribes(IVirtualFileSystem vfs, string path) =>
        ReferenceEquals(_pendingViewStateListing.Vfs, vfs)
        && string.Equals(_pendingViewStateListing.Path, path,
            ReferenceEquals(vfs, _vfs) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// True while a load newer than the listing on screen is under way. A delete reloads the folder
    /// itself and the watcher reloads it again 300 ms after the last file went: the view restores the
    /// cursor and scroll only after the last of them, or the first one spends the saved state and the
    /// second drops the cursor on the first row. Loads start and land on the UI thread.
    /// </summary>
    public bool HasNewerLoadInFlight => _loadsStarted != _loadsApplied;

    /// <summary>Returns and clears the pending view state, or null if none was saved.</summary>
    public ViewState? ConsumePendingViewState()
    {
        var state = _pendingViewState;
        _pendingViewState = null;
        return state;
    }

    /// <summary>
    /// Where the cursor belongs once a refresh (<see cref="PrepareForRefresh"/>) has refilled the list. A
    /// cursor whose row is still listed stays, also when a key moved it while the folder was read. One
    /// whose row went takes the row with the saved name (a changed file is listed anew), else the row now
    /// in its place: the next one after a delete, the new last one when the last row went — that one used
    /// to send the cursor and the scroll to the top. Null for an empty list.
    /// </summary>
    public static FileEntryViewModel? CursorAfterRefresh(
        FileEntryViewModel? current, IReadOnlyList<FileEntryViewModel> rows, ViewState state)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        if (current is not null && rows.Contains(current))
        {
            return current;
        }

        return rows.FirstOrDefault(r => r.Name == state.FocusedItemName)
            ?? rows[Math.Clamp(state.FocusedItemIndex, 0, rows.Count - 1)];
    }

    /// <summary>
    /// True when the panel shows something other than the plain local disk: an archive, a server, or
    /// search results. Whether its entries still are disk files is <see cref="EntriesOnDisk"/>.
    /// </summary>
    public bool IsMounted => _activeVfs != _vfs;

    /// <summary>True only for archive browsing — remote file systems are mounted but not archives.</summary>
    public bool IsInsideArchive => IsMounted && _mountKind == MountKind.Archive;

    /// <summary>True while a remote (FTP/SFTP) file system is mounted.</summary>
    public bool IsRemote => IsMounted && _mountKind == MountKind.Remote;

    /// <summary>
    /// True while the panel lists search hits (TRAP-28): mounted, yet every hit is a file under its
    /// own path — on the disk, or (a search on a server or in an archive, shown in the panel) inside
    /// the mount the list was opened over. Which of the two is <see cref="EntriesOnDisk"/>.
    /// </summary>
    public bool IsSearchResults => IsMounted && _mountKind == MountKind.SearchResults;

    /// <summary>
    /// True when every entry the panel lists is a file on the local disk, addressable by its Windows
    /// path (<c>ToWindowsPath</c>): the plain disk and the search results of a disk search, not an
    /// archive or a server, nor the results of a search there. What hands the selection to Windows —
    /// the Recycle Bin, shell verbs, packing, tools — asks this rather than <see cref="IsMounted"/>
    /// (INV-07).
    /// </summary>
    public bool EntriesOnDisk => !IsMounted || (IsSearchResults && FileSystem.FileSystemIdentity.IsLocalDisk(_activeVfs));

    /// <summary>
    /// The disk folder the panel stands in, as a Windows path, or null when it stands in none: an
    /// archive, a server, or the search-results list itself, whose "/" is not a folder (a hit's
    /// folder opened from the list is one). Where something is to be <i>put</i> asks this.
    /// </summary>
    public string? DiskFolder =>
        !IsMounted ? CurrentPath
        : IsSearchResults && EntriesOnDisk && !FileSystem.SearchResultsFileSystem.IsSyntheticPath(new VfsPath(CurrentPath))
            ? FileSystem.WindowsFileSystem.ToWindowsPath(new VfsPath(CurrentPath))
            : null;

    /// <summary>
    /// The disk folder the panel works in — where a tool, an editor or an agent starts and a project
    /// is looked for: <see cref="DiskFolder"/> where there is one. The search-results list is no
    /// folder, so it works in the disk folder the hits were listed over (where the panel stood, by
    /// default where the search ran) — the folder Ctrl+F and Ctrl+D already take for a mount. Null
    /// for an archive or a server, whose paths mean nothing to Windows.
    /// </summary>
    public string? WorkingFolder => DiskFolder ?? (IsSearchResults && EntriesOnDisk ? RootLocalPath : null);

    /// <summary>
    /// The folder the Windows shell menu is shown for over <paramref name="items"/> (empty = the
    /// folder's own menu), or null when it cannot be: an archive's or a server's entries have no
    /// Windows path. Search hits are disk files (TRAP-28) and get the shell menu when they lie in one
    /// folder: the shell resolves every item against the first one's folder, so hits from several
    /// folders would name other files there (a Delete on the wrong file). Those, and the list's own
    /// empty space, which is no folder, keep the manager's menu (TRAP-63).
    /// </summary>
    public string? ShellMenuFolderFor(IReadOnlyList<IFileEntry> items)
    {
        if (!EntriesOnDisk)
        {
            return null;
        }

        if (!IsSearchResults)
        {
            return CurrentPath;
        }

        if (items.Count == 0)
        {
            return DiskFolder;
        }

        return FileSystem.SearchResultsFileSystem.SharedFolder(items) is { } shared
            ? FileSystem.WindowsFileSystem.ToWindowsPath(shared)
            : null;
    }

    public IVirtualFileSystem ActiveVfs => _activeVfs;

    /// <summary>
    /// The disk file system underneath every mount — what <see cref="DiskFolder"/> and
    /// <see cref="WorkingFolder"/> are folders of.
    /// </summary>
    public IVirtualFileSystem DiskVfs => _vfs;

    /// <summary>
    /// The local-disk path the panel returns to once every mount is popped. Equals
    /// <see cref="CurrentPath"/> when nothing is mounted — this is what gets persisted,
    /// since an archive or FTP path is meaningless on the next launch.
    /// </summary>
    public string RootLocalPath =>
        _navStack.Count > 0 ? _navStack.Last().Path : CurrentPath;

    public FileListViewModel(IVirtualFileSystem vfs, ILogger<FileListViewModel> logger)
    {
        _activeVfs = vfs;
        _vfs = vfs;
        _logger = logger;

        // Marks changed by anything but the Shift run itself (Esc, Insert, Ctrl+click, a click that
        // collapses them on the run's last row) end the run: the next Shift move starts at the cursor
        // instead of bringing back the marks from before the run.
        SelectedFiles.CollectionChanged += (_, _) =>
        {
            if (!_applyingMarkRun)
            {
                _markRunAnchor = null;
            }
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BreadcrumbSegments))]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    private string _quickSearchText = string.Empty;

    /// <summary>
    /// The filter box under the path bar is open: by its button, or because a name is being typed into
    /// the list. Closing it drops the filter; moving to another folder closes it with the filter.
    /// </summary>
    [ObservableProperty]
    private bool _isFilterOpen;

    partial void OnIsFilterOpenChanged(bool value)
    {
        if (!value && QuickSearchText.Length > 0)
        {
            QuickSearchText = string.Empty;
        }
    }

    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private FileEntryViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isEditingPath;

    [ObservableProperty]
    private string _editablePath = string.Empty;

    /// <summary>
    /// Turns the path bar into a text box holding the current path — a click on the bar's empty part
    /// or the EditPath command. The bar puts the caret there when the box appears.
    /// </summary>
    public void BeginEditPath()
    {
        if (IsEditingPath)
        {
            return;   // already open: what was typed so far stays
        }

        EditablePath = CurrentPath;
        IsEditingPath = true;
    }

    [ObservableProperty]
    private SortColumn _sortBy = SortColumn.Name;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _foldersFirst = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanQuickFilter))]
    private ViewMode _viewMode = ViewMode.Details;

    /// <summary>
    /// The quick filter works on this view. The tree shows folders, not the list the filter narrows, so
    /// there typing jumps to a node by its name instead (<see cref="Navigation.TypeAhead"/>) and the
    /// filter's button and key are off (6.87).
    /// </summary>
    public bool CanQuickFilter => ViewMode != ViewMode.Tree;

    /// <summary>
    /// The quick search jumps instead of filtering (<see cref="Settings.AppSettings.QuickSearchJumps"/>):
    /// every name stays listed, the cursor goes to the best match, ↑ ↓ in the box go to the next one.
    /// </summary>
    [ObservableProperty]
    private bool _quickSearchJumps;

    partial void OnQuickSearchJumpsChanged(bool value)
    {
        if (QuickSearchText.Length > 0)
        {
            ApplyFilter();
        }
    }

    /// <summary>Some listed name answers the quick search; false paints the box as a miss.</summary>
    [ObservableProperty]
    private bool _quickSearchHasMatch = true;

    [ObservableProperty]
    private bool _showExtensions = true;

    /// <summary>
    /// Hide entries the working copy's <c>.gitignore</c> excludes (6.20). Has no effect outside a
    /// repository or on a mounted panel, where there are no rules to apply.
    /// </summary>
    [ObservableProperty]
    private bool _hideIgnored;

    /// <summary>Rules for <see cref="CurrentPath"/>, rebuilt on each load while the filter is on.</summary>
    private GitignoreLookup _ignoreLookup = GitignoreLookup.None;

    /// <summary>True when the current directory is inside a git working copy.</summary>
    public bool IsInRepository => _ignoreLookup.IsActive;

    /// <summary>Color rules applied when building FileEntryViewModels.</summary>
    public IReadOnlyList<ColorRule> ColorRules { get; set; } = Array.Empty<ColorRule>();

    // ── Details columns (6.19) ────────────────────────────────────────────────

    private IReadOnlyList<ColumnLayout> _columns = ColumnCatalog.DefaultLayout();

    /// <summary>
    /// Columns of the details view, in display order. Assigning rebuilds the grid's columns and,
    /// when a measured column was switched on, starts the background pass that fills it.
    /// </summary>
    public IReadOnlyList<ColumnLayout> Columns
    {
        get => _columns;
        set
        {
            _columns = ColumnCatalog.Normalize(value.ToList());
            ColumnsChanged?.Invoke(this, EventArgs.Empty);
            _ = RefreshMetricsAsync();
            _ = RefreshGitStatusAsync();
        }
    }

    /// <summary>Raised when <see cref="Columns"/> changed and the grid must rebuild its columns.</summary>
    public event EventHandler? ColumnsChanged;

    /// <summary>
    /// Raised before the layout is handed to the settings, so the view can copy the widths the
    /// user dragged back into it. The ViewModel cannot read them itself — the actual width of a
    /// column only exists on the grid.
    /// </summary>
    public event EventHandler? ColumnWidthsRequested;

    /// <summary>True when a column that needs measuring (LOC, size on disk) is switched on.</summary>
    public bool NeedsMetrics => _columns.Any(c =>
        c.Visible && ColumnCatalog.Find(c.Id) is { IsComputed: true });

    private readonly FileMetricsService _metrics = new();
    private CancellationTokenSource _metricsCts = new();

    public BulkObservableCollection<FileEntryViewModel> Files { get; } = new();
    public ObservableCollection<FileEntryViewModel> SelectedFiles { get; } = new();

    /// <summary>Raised on the calling thread after a directory load completes and Files is populated.</summary>
    public event EventHandler? DirectoryLoaded;

    /// <summary>
    /// Raised on the UI thread after a folder on the local disk was listed, with the raw listing, so
    /// the other folder trees (the docked one, the other panel's Tree view) can update that folder's
    /// node without reading it again. Not raised for a mounted panel: the trees show the disk only.
    /// </summary>
    public event EventHandler<DiskListingEventArgs>? DiskListingLoaded;

    public IReadOnlyList<BreadcrumbSegmentViewModel> BreadcrumbSegments =>
        BuildBreadcrumbs(CurrentPath, IsMounted, IsMounted ? MountRootLabel : null);

    /// <summary>
    /// What the root of the mounted file system is called in the path bar: the archive's file name,
    /// the server's address. Without it the bar was empty at the root of an archive or a server.
    /// </summary>
    public string MountRootLabel => IsSearchResults
        ? Localization.LocalizationManager.Get("Breadcrumb_SearchRoot")
        : DescribeMountRoot(_mountKind, _activeVfs.Scheme, _activeVfs.DisplayName);

    /// <summary>
    /// The name of a mount's root as the rest of the program shows it — the file system's own display
    /// name (<c>dist.zip</c>, <c>sftp://me@host</c>). A server whose name carries no scheme gets it in
    /// front (<c>ftp://me@host</c>), so an FTP root does not read like a folder called "me@host".
    /// </summary>
    public static string DescribeMountRoot(MountKind kind, string scheme, string displayName)
    {
        var name = displayName.Trim();
        if (kind == MountKind.Remote && scheme.Length > 0 && !name.Contains("://", StringComparison.Ordinal))
        {
            name = name.Length > 0 ? $"{scheme}://{name}" : $"{scheme}://";
        }

        return name.Length > 0 ? name : "/";
    }

    /// <summary>
    /// Clickable segments of <paramref name="path"/>. A mounted panel keeps rooted VFS paths
    /// ("/pub/logs"); on disk the first segment is the whole root — "C:\" or "\\server\share" —
    /// since a UNC server name alone is not a folder, and a bare "server\" would resolve
    /// against the process's working directory.
    /// </summary>
    /// <param name="mountRootLabel">For a mounted panel, the name of the mount's root
    /// (<see cref="MountRootLabel"/>): it becomes the first segment, leading to "/".</param>
    public static IReadOnlyList<BreadcrumbSegmentViewModel> BuildBreadcrumbs(string path, bool mounted, string? mountRootLabel = null)
    {
        var segments = BuildBreadcrumbSegments(path, mounted, mountRootLabel);
        if (segments.Count > 0)
        {
            segments[0].IsFirst = true;
            segments[^1].IsLast = true;
        }

        return segments;
    }

    private static List<BreadcrumbSegmentViewModel> BuildBreadcrumbSegments(string path, bool mounted, string? mountRootLabel)
    {
        var segments = new List<BreadcrumbSegmentViewModel>();
        if (string.IsNullOrEmpty(path))
        {
            return segments;
        }

        if (mounted)
        {
            if (!string.IsNullOrEmpty(mountRootLabel))
            {
                segments.Add(new BreadcrumbSegmentViewModel { Path = "/", DisplayName = mountRootLabel });
            }

            var vfsPath = string.Empty;
            foreach (var part in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            {
                vfsPath += "/" + part;
                segments.Add(new BreadcrumbSegmentViewModel { Path = vfsPath, DisplayName = part });
            }
            return segments;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        var accumulated = string.Empty;
        if (root.Length > 0)
        {
            accumulated = root.EndsWith('\\') || root.EndsWith('/') ? root : root + "\\";
            var rootName = root.TrimEnd('\\', '/');
            segments.Add(new BreadcrumbSegmentViewModel { Path = accumulated, DisplayName = rootName.Length > 0 ? rootName : root });
        }

        foreach (var part in path[root.Length..].Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            accumulated = accumulated.Length == 0 ? part : Path.Combine(accumulated, part);
            segments.Add(new BreadcrumbSegmentViewModel { Path = accumulated, DisplayName = part });
        }
        return segments;
    }

    partial void OnCurrentPathChanged(string value)
    {
        // A quick-search filter belongs to the folder it was typed in. Left on, it silently hides
        // most of the next folder's listing (type "src", press Enter, and the new folder looks empty).
        // The load that follows applies the cleared filter, so no separate refresh is needed.
        if (QuickSearchText.Length > 0)
        {
            _quickSearchText = string.Empty;
            OnPropertyChanged(nameof(QuickSearchText));
        }

        IsFilterOpen = false;

        if (!string.IsNullOrEmpty(value))
        {
            _ = LoadAsync(value);
            if (ViewMode == ViewMode.Tree)
                _ = ExpandTreeToPathAsync(value);
        }
    }

    partial void OnViewModeChanged(ViewMode value)
    {
        // A filter left open over the tree would hide files in a list the tree does not show (6.87).
        if (value == ViewMode.Tree)
        {
            IsFilterOpen = false;
        }

        if (_suppressViewStateReactions) return;
        if (value == ViewMode.Tree && !string.IsNullOrEmpty(CurrentPath))
            _ = BuildTreeAsync(CurrentPath);
    }

    partial void OnQuickSearchTextChanged(string value)
    {
        if (value.Length > 0)
        {
            IsFilterOpen = true;
        }

        ApplyFilter();
    }
    partial void OnSortByChanged(SortColumn value) { if (!_suppressApplySort && !_suppressViewStateReactions) ApplySort(); }
    partial void OnSortDescendingChanged(bool value) { if (!_suppressApplySort && !_suppressViewStateReactions) ApplySort(); }
    partial void OnFoldersFirstChanged(bool value) { if (!_suppressViewStateReactions) ApplySort(); }
    partial void OnShowHiddenFilesChanged(bool value) { if (!_suppressViewStateReactions) ApplyFilter(); }

    partial void OnHideIgnoredChanged(bool value)
    {
        if (_suppressViewStateReactions) return;
        // Turning the filter on for the first time in this directory needs its rules read.
        _ = ReloadIgnoreRulesAsync().ContinueWith(
            _ => System.Windows.Application.Current?.Dispatcher.InvokeAsync(ApplyFilter),
            TaskScheduler.Default);
    }

    /// <summary>
    /// Reads the <c>.gitignore</c> stack for the current directory off the UI thread (INV-06).
    /// Skipped entirely while the filter is off or the panel is mounted — a rule file inside an
    /// archive or on an FTP host describes someone else's working copy, not this listing.
    /// </summary>
    private async Task ReloadIgnoreRulesAsync()
    {
        if (!HideIgnored || IsMounted || string.IsNullOrEmpty(CurrentPath))
        {
            _ignoreLookup = GitignoreLookup.None;
            return;
        }

        var path = CurrentPath;
        _ignoreLookup = await Task.Run(() => GitignoreLookup.ForDirectory(path));
    }

    /// <summary>
    /// Flips the gitignore filter and waits for the rules to be read. Callers that report on
    /// <see cref="IsInRepository"/> must use this rather than the property: setting
    /// <see cref="HideIgnored"/> only <i>starts</i> the reload, so the flag still describes the
    /// previous directory when the setter returns.
    /// </summary>
    public async Task ToggleHideIgnoredAsync()
    {
        // The reaction is suppressed and the reload driven from here instead, so the rules are
        // read once rather than concurrently by the property handler and by this method.
        _suppressViewStateReactions = true;
        try { HideIgnored = !HideIgnored; }
        finally { _suppressViewStateReactions = false; }

        await ReloadIgnoreRulesAsync();
        ApplyFilter();
    }

    private Agents.FileOriginIndex _origins = Agents.FileOriginIndex.Empty;

    /// <summary>
    /// Who changed what in the repository the window is looking at (<see cref="ProjectStatusViewModel"/>);
    /// the rows of a disk listing carry the matching origin mark. Set again whenever the verdicts change.
    /// </summary>
    public Agents.FileOriginIndex Origins
    {
        get => _origins;
        set
        {
            _origins = value ?? Agents.FileOriginIndex.Empty;
            MarkOrigins(IsMounted ? null : CurrentPath);
        }
    }

    /// <summary>Marks every row of <paramref name="folder"/> from <see cref="Origins"/>; a mounted listing gets none.</summary>
    private void MarkOrigins(string? folder)
    {
        foreach (var row in _vmCache.Values)
        {
            if (folder is null || row.IsParentEntry)
            {
                row.SetOrigin(Agents.FileOriginMark.None, TCV.Ai.Agents.ChangeOrigin.External);
                continue;
            }

            var (mark, origin) = _origins.Lookup(Path.Combine(folder, row.Name), row.IsDirectory);
            row.SetOrigin(mark, origin);
        }
    }

    public async Task LoadAsync(string path)
    {
        DropNameEditForLoad();
        BeforeDirectoryLoad?.Invoke(this, EventArgs.Empty);
        var generation = ++_loadsStarted;

        // Sizes still being measured for another folder or mount are for rows this listing will not show;
        // a refresh of the same folder keeps its rows, and the walk goes on.
        if (_folderSizeJob is { } sizes && !IsFolderSizeJobCurrent(sizes, path))
        {
            CancelFolderSizes();
        }

        _loadCts.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        var onDisk = !IsMounted;
        var listedVfs = _activeVfs;
        try
        {
            var vfsPath = new VfsPath(path);
            // Kept to itself until applied: a load it overlaps reads into the same field, and the rows
            // must be built from this listing, not from whichever landed last.
            var listing = await _activeVfs.ListAsync(vfsPath, ct);

            // The disk lister reports a missing folder as an empty one. Say which it is, or a
            // mistyped path looks like a folder with nothing in it.
            string? notFound = null;
            if (listing.Count == 0 && !IsMounted && await _activeVfs.GetEntryAsync(vfsPath, ct) is null)
            {
                notFound = Localization.LocalizationManager.Get("Files_NotFound");
            }

            await ReloadIgnoreRulesAsync();
            await OnDispatcherAsync(() =>
            {
                // A newer load started after this one read the folder: its rows are the ones to show.
                // Applying these as well would put an older listing over it and its generation under
                // the newer one's, and the view would wait for a load that has already landed.
                if (generation != _loadsStarted)
                {
                    return;
                }

                DropNameEditBeforeRowsChange();

                // A refresh saved from other rows — the watcher's reload overtaken by a folder opened, or
                // asked for once the panel had moved on — has nothing to put back here.
                if (_pendingViewState is not null && !PendingViewStateDescribes(listedVfs, path))
                {
                    _pendingViewState = null;
                }

                _listedVfs = listedVfs;
                _listedPath = path;
                _allEntries = listing;
                LoadError = notFound;
                // Rebuild cache: reuse existing VMs for unchanged entries, create new ones otherwise
                var newCache = new Dictionary<string, FileEntryViewModel>(listing.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in listing)
                {
                    var key = CacheKey(entry);
                    var expectedSize = entry.IsDirectory ? 0 : entry.Size;
                    if (_vmCache.TryGetValue(key, out var existing) &&
                        existing.Entry.Path.Equals(entry.Path) &&
                        existing.Size == expectedSize &&
                        existing.LastModified == entry.LastModified &&
                        existing.Attributes == entry.Attributes)
                    {
                        newCache[key] = existing;
                    }
                    else
                    {
                        newCache[key] = new FileEntryViewModel(entry, ColorRules);
                    }
                }
                _vmCache.Clear();
                foreach (var kvp in newCache) _vmCache[kvp.Key] = kvp.Value;

                PruneSelection(_vmCache.Values);
                MarkOrigins(onDisk ? path : null);
                ApplyFilter();

                // A refresh that took the cursor's row away (F8, F6, the watcher) puts the cursor on its
                // successor with the new rows. The view brings back the scroll and the focus only after
                // the layout, later still when the watcher's reload overtakes it; until then the panel had
                // no cursor, so ↓ started from the top and scrolled there, and F8 did nothing (TRAP-87).
                // A name to land on (a folder left, F6 renaming in place) is the view's to find.
                if (_pendingViewState is { } refresh && _pendingSelectName is null)
                {
                    SelectedItem = CursorAfterRefresh(SelectedItem, Files, refresh);
                }

                _loadsApplied = generation;
                DirectoryLoaded?.Invoke(this, EventArgs.Empty);

                // The folder trees show this folder too: their node takes the fresh listing instead of
                // staying as it was first read — a folder made, deleted or renamed here, by this
                // program or by anything else the watcher saw, shows up in the tree as well.
                if (onDisk && !ct.IsCancellationRequested)
                {
                    ApplyListingToTree(path, listing);
                    DiskListingLoaded?.Invoke(this, new DiskListingEventArgs(path, listing));
                }
            });
            _logger.LogDebug("Loaded {Count} entries from {Path}", listing.Count, path);
            _ = RefreshMetricsAsync();
            _ = RefreshGitStatusAsync();
            if (!ct.IsCancellationRequested)
            {
                RaiseListingCompleted(path, null);
            }
        }
        // Only a newer load cancels this one. A file system's own cancellation (an HTTP timeout) is a
        // failure: it is shown, it may reconnect (ReconnectPolicy), and the load is no longer in flight.
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load {Path}", path);
            if (!ct.IsCancellationRequested)
            {
                await ShowLoadFailureAsync(ex, generation);
                RaiseListingCompleted(path, ex);   // a dropped connection reconnects by itself (6.82)
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    /// <summary>
    /// A listing that could not be read must not leave the previous folder's files on screen under
    /// the new path: every command would then act on a folder the panel no longer shows.
    /// </summary>
    private async Task ShowLoadFailureAsync(Exception ex, int generation)
    {
        void Apply()
        {
            // A load started since shows its own outcome; this one must not count it as landed.
            if (generation != _loadsStarted)
            {
                return;
            }

            _loadsApplied = generation;
            DropNameEditBeforeRowsChange();
            _listedVfs = null;
            _listedPath = null;
            _allEntries = Array.Empty<IFileEntry>();
            _vmCache.Clear();
            LoadError = string.Format(Localization.LocalizationManager.Get("Files_LoadFailed"), ex.Message);
            PruneSelection(Array.Empty<FileEntryViewModel>());
            ApplyFilter();
            DirectoryLoaded?.Invoke(this, EventArgs.Empty);
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else await dispatcher.InvokeAsync(Apply);
    }

    /// <summary>Why the list is empty ("nothing here", "no match", the load error), or empty text when it is not.</summary>
    [ObservableProperty]
    private string _emptyStateText = string.Empty;

    /// <summary>Set when the last load failed or the folder does not exist; cleared by the next good load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(IsConnectionFailed))]
    private string? _loadError;

    public bool HasLoadError => LoadError is not null;

    /// <summary>
    /// The server under the panel did not answer: the list offers Reconnect and Disconnect right under
    /// the error, instead of a dialog (a dropped connection used to just sit there).
    /// </summary>
    public bool IsConnectionFailed => HasLoadError && ActiveConnection is not null && !IsReconnecting;

    partial void OnIsLoadingChanged(bool value) => UpdateEmptyState();

    private void UpdateEmptyState()
    {
        // A dropped connection being opened again says so instead of the error that dropped it.
        if (ReconnectStatus is not null)
        {
            EmptyStateText = ReconnectStatus;
            return;
        }

        if (LoadError is not null)
        {
            EmptyStateText = LoadError;
            return;
        }

        var hasRows = Files.Any(f => !f.IsParentEntry);
        EmptyStateText = hasRows || IsLoading
            ? string.Empty
            : Localization.LocalizationManager.Get(
                string.IsNullOrWhiteSpace(QuickSearchText) ? "Files_Empty" : "Files_NoMatches");
    }

    /// <summary>
    /// Fills the measured columns (LOC, size on disk) for the current listing in the background.
    /// A new call cancels the previous pass — navigating fast must not queue up work for
    /// directories nobody is looking at any more.
    /// </summary>
    /// <remarks>
    /// Only ever runs on the local disk: both measurements need a real Windows path, and a mounted
    /// panel does not have one (INV-07 / TRAP-02). Inside an archive or on FTP the cells stay blank.
    /// </remarks>
    // The rows the grid is showing (first and last index into Files), reported by the view as it scrolls.
    // Until it reports, the top of the list is assumed — that is what opens on screen.
    private int _visibleFirst;
    private int _visibleLast = 59;
    private CancellationTokenSource? _metricsDebounce;

    /// <summary>Rows measured beyond the visible ones, so a small scroll finds its cells already filled.</summary>
    private const int MetricsMargin = 20;

    /// <summary>Rows measured per hop back to the UI thread, so cells fill in progressively.</summary>
    private const int MetricsChunk = 25;

    /// <summary>
    /// Tells the panel which rows are on screen. The measured columns (LOC, size on disk) cost real IO,
    /// so only these rows — plus a small margin — are measured, and only once scrolling has settled.
    /// </summary>
    public void SetVisibleRange(int first, int last)
    {
        if (first == _visibleFirst && last == _visibleLast)
        {
            return;
        }
        _visibleFirst = Math.Max(0, first);
        _visibleLast = Math.Max(_visibleFirst, last);

        if (!NeedsMetrics || !EntriesOnDisk)
        {
            return;
        }

        // A drag of the scroll bar reports dozens of ranges a second; only the resting one is worth reading.
        _metricsDebounce?.Cancel();
        var debounce = _metricsDebounce = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMilliseconds(150), debounce.Token).ContinueWith(
            t =>
            {
                if (t.IsCanceled)
                {
                    return;
                }
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _ = RefreshMetricsAsync());
            },
            TaskScheduler.Default);
    }

    private async Task RefreshMetricsAsync()
    {
        _metricsCts.Cancel();
        _metricsCts = new CancellationTokenSource();
        var ct = _metricsCts.Token;

        if (!NeedsMetrics || !EntriesOnDisk) return;

        // Rows in view (plus a margin) that have not been measured yet: never the whole folder.
        var snapshot = Files.ToList();
        if (snapshot.Count == 0)
        {
            return;
        }
        var from = Math.Clamp(_visibleFirst - MetricsMargin, 0, snapshot.Count - 1);
        var to = Math.Clamp(_visibleLast + MetricsMargin, from, snapshot.Count - 1);
        var targets = snapshot.Skip(from).Take(to - from + 1)
            .Where(f => !f.IsParentEntry && !f.IsDirectory && f.Entry is not null && !f.MetricsMeasured)
            .ToList();
        if (targets.Count == 0) return;

        var budget = new FileMetricsService.ReadBudget(FileMetricsService.DefaultPassBudgetBytes);

        try
        {
            foreach (var chunk in targets.Chunk(MetricsChunk))
            {
                if (ct.IsCancellationRequested) return;

                // One hop to a worker per chunk, not per file; values are captured here, on the UI thread.
                var work = chunk.Select(vm => (
                    Vm: vm,
                    Path: FileSystem.WindowsFileSystem.ToWindowsPath(vm.Entry.Path),
                    vm.Size,
                    vm.LastModified)).ToArray();

                var measured = await Task.Run(() => work
                    .Select(w => _metrics.Measure(w.Path, w.Size, w.LastModified, false, budget))
                    .ToArray(), ct);

                if (ct.IsCancellationRequested) return;
                for (var i = 0; i < work.Length; i++)
                {
                    work[i].Vm.LineCount = measured[i].Lines;
                    work[i].Vm.SizeOnDisk = measured[i].SizeOnDisk;
                    // A file skipped for lack of budget stays unmeasured so a later pass (a scroll) retries it.
                    work[i].Vm.MetricsMeasured = !measured[i].Deferred;
                }
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer pass */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Column metrics pass failed for {Path}", CurrentPath);
        }
    }

    /// <summary>True when the Git column is switched on.</summary>
    public bool NeedsGitStatus => _columns.Any(c => c.Visible && c.Id == ColumnCatalog.Git);

    /// <summary>Stateless (opens and disposes the repository per call), so one instance serves every list.</summary>
    private static readonly GitStatusProvider GitStatus =
        new(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitStatusProvider>.Instance);

    private CancellationTokenSource _gitCts = new();

    /// <summary>
    /// Fills the Git column for the current listing with one status pass on a background thread
    /// (INV-06). A newer pass cancels the older one; a mounted panel has no repository and is skipped.
    /// The answer is keyed by the row's own name within this folder, so a change in
    /// <c>sub/a.cs</c> cannot paint a sibling <c>a.cs</c>.
    /// </summary>
    private async Task RefreshGitStatusAsync()
    {
        _gitCts.Cancel();
        _gitCts = new CancellationTokenSource();
        var ct = _gitCts.Token;

        if (!NeedsGitStatus || !EntriesOnDisk || string.IsNullOrEmpty(CurrentPath)) return;

        var path = CurrentPath;
        try
        {
            // A folder is one status pass. Search hits lie in many folders: one pass per folder, each
            // row answered by its own folder's status.
            var rows = _vmCache.Values.Where(vm => !vm.IsParentEntry).ToList();
            var folders = IsSearchResults
                ? rows.GroupBy(vm => vm.Entry.Path.Parent?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Key.Length > 0)
                    .Select(g => (Folder: FileSystem.WindowsFileSystem.ToWindowsPath(new VfsPath(g.Key)), Rows: g.ToList()))
                    .ToList()
                : [(Folder: path, Rows: rows)];

            foreach (var (folder, folderRows) in folders)
            {
                var status = await GitStatus.GetDirectoryStatusAsync(folder, ct);
                if (ct.IsCancellationRequested || path != CurrentPath) return;

                foreach (var vm in folderRows)
                {
                    var cell = status.Get(vm.Name);
                    vm.GitLabel = GitCellText.Short(cell.Label);
                    vm.GitKind = cell.Kind;
                }
            }
        }
        catch (OperationCanceledException) { /* superseded by a newer pass */ }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Git column pass failed for {Path}", path);
        }
    }

    private QuickFilter _quickFilter = QuickFilter.Empty;

    private void ApplyFilter()
    {
        var filter = QuickFilter.Parse(QuickSearchText);
        var queryChanged = filter.Text != _quickFilter.Text;
        _quickFilter = filter;

        // Jumping, the search narrows nothing: it only moves the cursor (6.87).
        var filtered = VisibleEntries();
        if (!filter.IsEmpty && !QuickSearchJumps)
        {
            filtered = filtered.Where(e => filter.Matches(e.Name));
        }

        var sorted = Sort(filtered);
        Files.ResetIfChanged(BuildViewModels(sorted).ToList());

        // What the filter hides must not stay selected: F8 would delete files the panel is not showing.
        PruneSelection(Files);

        if (!filter.IsEmpty && CursorAfterFilter(SelectedItem, Files, filter, queryChanged) is { } cursor)
        {
            SelectedItem = cursor;
        }
        else if (SelectedItem is null && _lastCursor is { } last && Files.Contains(last))
        {
            SelectedItem = last;
        }

        QuickSearchHasMatch = filter.IsEmpty || Files.Any(f => !f.IsParentEntry && filter.Matches(f.Name));
        UpdateEmptyState();
    }

    /// <summary>
    /// ↑ or ↓ in the quick search box while it jumps (<see cref="QuickSearchJumps"/>): the cursor goes to
    /// the previous or next name that matches, past the ones that do not. Stays put at the last match.
    /// </summary>
    public void MoveToNextMatch(bool forward, bool extendMarks)
    {
        if (_quickFilter.IsEmpty)
        {
            return;
        }

        var from = SelectedItem is { } cursor ? Files.IndexOf(cursor) : -1;
        var names = Files.Select(f => f.IsParentEntry ? null : f.Name).ToList();
        if (_quickFilter.NextMatch(names, from, forward) is var next and >= 0)
        {
            MoveCursorTo(next, extendMarks);
        }
    }

    // The last row the cursor stood on. A filter that matches nothing empties the cursor (the grid lets go
    // of the row it no longer shows and writes null back); when the rows come back, so does the cursor —
    // after Esc the list used to stand with no cursor at all, and the arrows did nothing.
    private FileEntryViewModel? _lastCursor;

    partial void OnSelectedItemChanged(FileEntryViewModel? value)
    {
        if (value is not null)
        {
            _lastCursor = value;
        }
    }

    /// <summary>
    /// Where the cursor belongs once the quick filter has narrowed the list. A query just typed takes it
    /// to the best match (<see cref="QuickFilterMatch"/>), the first in the list's order: typing "wind"
    /// lands on "WinDevCleaner.App" even when "MainWindow.xbf" and a column of "Microsoft.Windows.*"
    /// come before it. The cursor stays put when it already is on as good a match, so a match picked with
    /// ↓ survives the next letter. A refill under the same query (a reload, a sort) only moves it off a
    /// row the filter hid. Typing a name and pressing Enter has to open that name: the cursor used to stay
    /// on ".." (Enter went up a level) or on the first row that merely contained the text. Null when
    /// nothing matches.
    /// </summary>
    public static FileEntryViewModel? CursorAfterFilter(
        FileEntryViewModel? current, IReadOnlyList<FileEntryViewModel> rows, QuickFilter filter, bool queryChanged)
    {
        if (!queryChanged && current is not null && rows.Contains(current))
        {
            return current;
        }

        FileEntryViewModel? best = null;
        var bestMatch = QuickFilterMatch.None;
        foreach (var row in rows)
        {
            if (row.IsParentEntry)
            {
                continue;
            }

            var match = filter.Match(row.Name);
            if (match > bestMatch)
            {
                (best, bestMatch) = (row, match);
            }
        }

        if (current is { IsParentEntry: false } && rows.Contains(current) && filter.Match(current.Name) >= bestMatch)
        {
            return current;
        }

        return best;
    }

    // Re-sorting is re-filtering: sorting from VisibleEntries() alone dropped the quick filter
    // while its badge stayed on screen.
    private void ApplySort() => ApplyFilter();

    /// <summary>The listing minus what the panel's attribute and gitignore filters hide.</summary>
    private IEnumerable<IFileEntry> VisibleEntries()
    {
        IEnumerable<IFileEntry> entries = _allEntries;

        if (!ShowHiddenFiles)
            entries = entries.Where(e => !e.Attributes.HasFlag(FileAttributes.Hidden));

        if (HideIgnored && _ignoreLookup.IsActive)
            entries = entries.Where(e => !_ignoreLookup.IsIgnored(e.Name, e.IsDirectory));

        return entries;
    }

    private IEnumerable<FileEntryViewModel> BuildViewModels(IEnumerable<IFileEntry> entries)
    {
        // Prepend ".." entry when inside a subdirectory (not at drive root).
        // A mounted VFS always shows it — at its root ".." leaves the archive/connection.
        if (!string.IsNullOrEmpty(CurrentPath))
        {
            var parentPath = IsMounted
                ? Path.GetDirectoryName(CurrentPath.TrimEnd('/', '\\'))
                : Path.GetDirectoryName(CurrentPath.TrimEnd('\\'));
            if (IsMounted || !string.IsNullOrEmpty(parentPath))
                yield return (_parentVm ??= FileEntryViewModel.CreateParent());
        }

        foreach (var entry in entries)
            if (_vmCache.TryGetValue(CacheKey(entry), out var vm))
                yield return vm;
    }

    /// <summary>
    /// What a row is known by between listings: its name within a folder. Search hits come from many
    /// folders, and two named <c>index.ts</c> are two rows — keyed by name, both showed (and F8
    /// deleted) the same file — so there it is the full path.
    /// </summary>
    private string CacheKey(IFileEntry entry) => IsSearchResults ? entry.Path.Value : entry.Name;

    // Natural name order ("file2" before "file10") with the name as tie-break for every column.
    private IEnumerable<IFileEntry> Sort(IEnumerable<IFileEntry> source) =>
        source.OrderBy(e => e, new FileEntryComparer(SortBy, SortDescending, FoldersFirst, MeasuredSize));

    // A folder sorts by the size measured for it (Space, «Folder sizes»), as 0 until then.
    private long MeasuredSize(IFileEntry entry) =>
        entry.IsDirectory && _vmCache.TryGetValue(CacheKey(entry), out var row) ? row.KnownSize : entry.Size;

    /// <summary>Called from the view when user clicks a column header. Toggles direction on same column.</summary>
    public void SetSort(SortColumn column)
    {
        _suppressApplySort = true;
        try
        {
            if (SortBy == column)
                SortDescending = !SortDescending;
            else
            {
                SortBy = column;
                SortDescending = false;
            }
        }
        finally
        {
            _suppressApplySort = false;
        }
        ApplySort();
    }

    /// <summary>
    /// Insert / Space: marks or unmarks the entry under the cursor and moves the cursor down, so a
    /// run of presses marks a run of files (Total Commander). Marks live in <c>IsSelected</c>, apart
    /// from the lists' own selection, which is only ever the cursor, so moving the cursor never drops them.
    /// </summary>
    public void ToggleSelectAndAdvance()
    {
        if (SelectedItem is not { } current) return;

        ToggleSelect(current);

        var index = Files.IndexOf(current);
        if (index >= 0 && index + 1 < Files.Count)
            SelectedItem = Files[index + 1];
    }

    // ──────────────────────────────── Folder sizes ────────────────────────────────

    /// <summary>
    /// The folders of one listing waiting to be measured, walked one after another: Space pressed on
    /// ten folders queues ten walks of the disk rather than running ten at once.
    /// </summary>
    private sealed class FolderSizeJob(IVirtualFileSystem fs, string folder)
    {
        public IVirtualFileSystem Fs { get; } = fs;
        public string Folder { get; } = folder;
        public CancellationTokenSource Cts { get; } = new();
        public Queue<FileEntryViewModel> Queue { get; } = new();

        /// <summary>The folder being walked now, out of the queue.</summary>
        public FileEntryViewModel? Current { get; set; }

        public bool Running { get; set; }

        /// <summary>Sort again once the queue is done: «Folder sizes» under a sort by size.</summary>
        public bool Resort { get; set; }
    }

    private FolderSizeJob? _folderSizeJob;

    /// <summary>Raised on the UI thread when a folder's size arrives, so the status line adds up the marked ones.</summary>
    public event EventHandler? FolderSizesChanged;

    /// <summary>
    /// Space: marks or unmarks the entry under the cursor and moves down, as Insert does; a folder it
    /// marks is measured as well, and its size shows in the size column and in the status line's total
    /// (Total Commander). Unmarking, or a folder measured before, measures nothing.
    /// </summary>
    public void ToggleSelectWithSizeAndAdvance()
    {
        if (SelectedItem is { IsDirectory: true, IsParentEntry: false, IsSelected: false, FolderSize: null } folder)
        {
            MeasureFolderSizes([folder], resort: false);
        }

        ToggleSelectAndAdvance();
    }

    /// <summary>
    /// «Folder sizes»: measures every folder in the listing — again for one measured before, whose content
    /// may have changed since — and sorts again when the panel is sorted by size.
    /// </summary>
    public void MeasureAllFolderSizes() =>
        MeasureFolderSizes(Files.Where(f => f.IsDirectory && !f.IsParentEntry).ToList(), resort: true);

    /// <summary>The newest folder-size walk, for tests and callers that need its end.</summary>
    public Task FolderSizesMeasured { get; private set; } = Task.CompletedTask;

    private void MeasureFolderSizes(IReadOnlyList<FileEntryViewModel> folders, bool resort)
    {
        if (folders.Count == 0 || string.IsNullOrEmpty(CurrentPath))
        {
            return;
        }

        if (_folderSizeJob is not { } job || !IsFolderSizeJobCurrent(job, CurrentPath))
        {
            CancelFolderSizes();
            job = _folderSizeJob = new FolderSizeJob(_activeVfs, CurrentPath);
        }

        job.Resort |= resort;
        foreach (var folder in folders)
        {
            if (folder.IsMeasuringSize)
            {
                continue;   // queued already
            }

            folder.IsMeasuringSize = true;
            job.Queue.Enqueue(folder);
        }

        if (!job.Running)
        {
            FolderSizesMeasured = RunFolderSizesAsync(job);
        }
    }

    private bool IsFolderSizeJobCurrent(FolderSizeJob job, string path) =>
        !job.Cts.IsCancellationRequested
        && ReferenceEquals(job.Fs, _activeVfs)
        && string.Equals(job.Folder, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stops measuring: the panel left the folder (or the mount) whose rows were waiting for a size.</summary>
    private void CancelFolderSizes()
    {
        if (_folderSizeJob is { } job)
        {
            job.Cts.Cancel();
            // Now rather than when the walk notices: a row shown again (a load cancelled before it
            // replaced the rows) must be free for a new walk, not skipped as queued by this one.
            ReleaseFolderSizeRows(job);
        }

        _folderSizeJob = null;
    }

    /// <summary>The rows a job still held stop waiting for a size.</summary>
    private static void ReleaseFolderSizeRows(FolderSizeJob job)
    {
        if (job.Current is { } current)
        {
            current.IsMeasuringSize = false;
        }

        foreach (var left in job.Queue)
        {
            left.IsMeasuringSize = false;
        }

        job.Current = null;
        job.Queue.Clear();
    }

    private async Task RunFolderSizesAsync(FolderSizeJob job)
    {
        job.Running = true;
        var ct = job.Cts.Token;
        Func<IFileEntry, bool> isLink = EntriesOnDisk ? IsDiskLink : FileSystem.FolderSizeCounter.ReparsePointIsLink;

        // The walk reads a mounted archive or server for as long as it takes: unmounting meanwhile must
        // not close it under the walk (INV-09). Taken here, on the UI thread, with the file system it reads.
        using var lease = LeaseActiveVfs();
        try
        {
            while (job.Queue.TryDequeue(out var folder))
            {
                ct.ThrowIfCancellationRequested();
                job.Current = folder;
                var path = folder.Entry.Path;
                FileSystem.FolderSizeCounter.Result result;
                try
                {
                    result = await Task.Run(() => FileSystem.FolderSizeCounter.CountAsync(job.Fs, path, isLink, ct), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // One folder that could not be walked leaves the rest of the queue to go on.
                    _logger.LogDebug(ex, "Measuring the size of {Path} failed", path);
                    folder.IsMeasuringSize = false;
                    job.Current = null;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                ApplyFolderSize(folder, result);
                job.Current = null;
            }

            if (job.Resort && SortBy == SortColumn.Size)
            {
                ApplySort();
            }
        }
        catch (OperationCanceledException)
        {
            // The panel left the folder; CancelFolderSizes released the rows.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Measuring folder sizes failed in {Path}", job.Folder);
        }
        finally
        {
            // A cancelled job's rows were released when it was cancelled, and may be queued by a new one.
            if (!ct.IsCancellationRequested)
            {
                ReleaseFolderSizeRows(job);
            }

            // «Folder sizes» sorts once, at the end of its walk: a later Space in the same folder does not.
            job.Resort = false;
            job.Running = false;
        }
    }

    private void ApplyFolderSize(FileEntryViewModel folder, FileSystem.FolderSizeCounter.Result result)
    {
        folder.FolderSize = result.Bytes;
        folder.FolderSizeComplete = result.Complete;
        folder.IsMeasuringSize = false;

        // A reload while the walk ran replaces the row of a folder that changed: the size goes to the
        // row on screen too. Only if it is the same folder: the rows are keyed by name, and a walk started
        // while another folder was loading (Space during a slow listing) would give its "src" size to
        // the new folder's "src".
        if (_vmCache.TryGetValue(CacheKey(folder.Entry), out var shown) && !ReferenceEquals(shown, folder)
            && shown.IsDirectory && shown.Entry.Path.Equals(folder.Entry.Path))
        {
            shown.FolderSize = result.Bytes;
            shown.FolderSizeComplete = result.Complete;
        }

        FolderSizesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A junction or a symbolic link on the disk. A reparse point alone is not enough: a OneDrive folder
    /// is one too, and its content is here (<see cref="DirectoryTree.IsLink"/>).
    /// </summary>
    private static bool IsDiskLink(IFileEntry entry) =>
        entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && DirectoryTree.IsLink(new DirectoryInfo(FileSystem.WindowsFileSystem.ToWindowsPath(entry.Path)));

    // A run of Shift+moves (Shift+End, Shift+arrows, Shift+clicks): the row it started on, the marks
    // that were there before it, whether it marks or unmarks, and where it left the cursor. It lasts
    // while nothing else moves the cursor or changes the marks.
    private FileEntryViewModel? _markRunAnchor;
    private HashSet<FileEntryViewModel>? _markRunBaseline;
    private bool _markRunAdds;
    private FileEntryViewModel? _markRunCursor;
    private bool _applyingMarkRun;

    /// <summary>
    /// Moves the cursor to row <paramref name="index"/>, clamped to the list (Home, End, and the arrows
    /// of the Thumbnails view). With <paramref name="extendMarks"/> — Shift held — every row from where
    /// the Shift run started to the new cursor takes the run's state, on top of the marks made before
    /// the run. A run that starts on an unmarked row marks (Shift+End marks everything from the cursor
    /// down); one that starts on a marked row unmarks, which is how a part of a select-all is taken back
    /// (Total Commander). Stepping back with Shift undoes only what the run itself did, never a file
    /// marked with Insert before it.
    /// </summary>
    public void MoveCursorTo(int index, bool extendMarks) => MoveCursorTo(index, extendMarks, adds: null);

    /// <summary>
    /// Shift+click: marks every row from where the Shift run started — the cursor, if no run is going
    /// on — to <paramref name="entry"/>, and moves the cursor there. A new run always marks, wherever it
    /// starts: a click after Ctrl+clicking a file extends the marks from it, as in Explorer. One already
    /// going on (Shift+arrows, an earlier Shift+click) keeps its own state.
    /// </summary>
    public void ExtendMarksTo(FileEntryViewModel entry)
    {
        var index = Files.IndexOf(entry);
        if (index >= 0)
        {
            MoveCursorTo(index, extendMarks: true, adds: true);
        }
    }

    private void MoveCursorTo(int index, bool extendMarks, bool? adds)
    {
        if (Files.Count == 0)
        {
            return;
        }

        var target = Files[Math.Clamp(index, 0, Files.Count - 1)];
        if (!extendMarks)
        {
            _markRunAnchor = null;
            _markRunBaseline = null;
            _markRunCursor = null;
            SelectedItem = target;
            return;
        }

        var continuing = _markRunAnchor is not null
            && _markRunBaseline is not null
            && ReferenceEquals(SelectedItem, _markRunCursor)
            && Files.Contains(_markRunAnchor);
        if (!continuing)
        {
            _markRunAnchor = SelectedItem is { } cursor && Files.Contains(cursor) ? cursor : target;
            _markRunBaseline = SelectedFiles.ToHashSet();
            _markRunAdds = adds ?? !_markRunAnchor.IsSelected;
        }

        var from = Files.IndexOf(_markRunAnchor!);
        var to = Files.IndexOf(target);
        var marks = new HashSet<FileEntryViewModel>(_markRunBaseline!);
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            if (_markRunAdds)
            {
                marks.Add(Files[i]);
            }
            else
            {
                marks.Remove(Files[i]);
            }
        }

        _applyingMarkRun = true;
        try
        {
            SetMarks(marks);
        }
        finally
        {
            _applyingMarkRun = false;
        }

        _markRunCursor = target;
        SelectedItem = target;
    }

    /// <summary>Makes exactly <paramref name="marked"/> the marked entries (the parent entry never is).</summary>
    public void SetMarks(IEnumerable<FileEntryViewModel> marked)
    {
        var want = marked.Where(m => !m.IsParentEntry).ToHashSet();

        for (var i = SelectedFiles.Count - 1; i >= 0; i--)
        {
            if (want.Contains(SelectedFiles[i])) continue;
            SelectedFiles[i].IsSelected = false;
            SelectedFiles.RemoveAt(i);
        }

        foreach (var m in want)
        {
            if (m.IsSelected) continue;
            m.IsSelected = true;
            SelectedFiles.Add(m);
        }
    }

    public void ToggleSelect(FileEntryViewModel vm)
    {
        if (vm.IsParentEntry) return;
        vm.IsSelected = !vm.IsSelected;
        if (vm.IsSelected)
            SelectedFiles.Add(vm);
        else
            SelectedFiles.Remove(vm);
    }

    /// <summary>
    /// Drops from <see cref="SelectedFiles"/> every view model <paramref name="listing"/> no longer
    /// contains. A selection belongs to the directory it was made in: without this, navigating away
    /// leaves the previous folder's entries selected, and the next command — copy, or worse,
    /// delete — acts on files the panel is not even showing. Entries that survived the load keep
    /// their view models, so refreshing in place still preserves the selection.
    /// </summary>
    public void PruneSelection(IEnumerable<FileEntryViewModel> listing)
    {
        // Identity, not name: a reload that replaced an entry's view model must drop the old one,
        // or the selection would keep pointing at a stale snapshot of the file.
        var kept = listing.ToHashSet();

        for (var i = SelectedFiles.Count - 1; i >= 0; i--)
        {
            if (kept.Contains(SelectedFiles[i])) continue;

            SelectedFiles[i].IsSelected = false;
            SelectedFiles.RemoveAt(i);
        }
    }

    public void SelectAll()
    {
        foreach (var f in Files)
            if (!f.IsParentEntry && !f.IsSelected) { f.IsSelected = true; SelectedFiles.Add(f); }
    }

    public void DeselectAll()
    {
        foreach (var f in Files) f.IsSelected = false;
        SelectedFiles.Clear();
    }

    public void InvertSelection()
    {
        foreach (var f in Files)
        {
            if (f.IsParentEntry) continue;
            f.IsSelected = !f.IsSelected;
            if (f.IsSelected)
                SelectedFiles.Add(f);
            else
                SelectedFiles.Remove(f);
        }
    }

    /// <summary>
    /// Selects (<paramref name="select"/>=true) or unselects the entries whose name matches the
    /// wildcard <paramref name="mask"/>. Multiple masks may be separated by ';', ',' or spaces,
    /// TC-style, and an entry matches if it matches any of them. <c>*</c> and <c>?</c> are honoured.
    /// </summary>
    public void SelectByMask(string mask, bool select)
    {
        if (string.IsNullOrWhiteSpace(mask)) return;

        var patterns = mask
            .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(WildcardToRegex)
            .ToList();
        if (patterns.Count == 0) return;

        foreach (var f in Files)
        {
            if (f.IsParentEntry) continue;
            if (!patterns.Any(re => re.IsMatch(f.Name))) continue;
            if (f.IsSelected == select) continue;

            f.IsSelected = select;
            if (select)
                SelectedFiles.Add(f);
            else
                SelectedFiles.Remove(f);
        }
    }

    private static Regex WildcardToRegex(string pattern)
    {
        // Total Commander/DOS convention: "*.*" matches every file, including extensionless ones
        // (README, LICENSE). A literal ".*" would demand a dot and skip those.
        if (pattern == "*.*") pattern = "*";

        return new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase);
    }

    [RelayCommand]
    private Task NavigateUp()
    {
        if (string.IsNullOrEmpty(CurrentPath)) return Task.CompletedTask;

        // Inside a mounted VFS (archive or remote): go up, and leave the mount only from its root.
        // "/docs" goes to "/", not out of the archive — GetDirectoryName("/docs") is "\", which
        // once looked like "already at the root".
        if (IsMounted)
        {
            var trimmed = CurrentPath.TrimEnd('/', '\\');
            if (trimmed.Length == 0)
            {
                // Back on the disk, the cursor belongs on the archive that was just left.
                _pendingSelectName = _activeVfs is TCV.Archives.ArchiveFileSystem archive
                    ? Path.GetFileName(archive.ArchivePath)
                    : null;
                Unmount();
                return Task.CompletedTask;
            }

            var parent = Path.GetDirectoryName(trimmed)?.Replace('\\', '/');
            _pendingSelectName = Path.GetFileName(trimmed);
            CurrentPath = string.IsNullOrEmpty(parent) ? "/" : parent;
            return Task.CompletedTask;
        }

        var currentTrimmed = CurrentPath.TrimEnd('\\', '/');
        _pendingSelectName = Path.GetFileName(currentTrimmed);
        var dir = Path.GetDirectoryName(currentTrimmed);
        if (dir is not null) CurrentPath = dir;
        return Task.CompletedTask;
    }

    // A refresh keeps the cursor, scroll position and marks where they were. In the Tree view it also
    // re-reads the drives and every open folder of the tree — the ones no panel is showing are not
    // watched, so this is how a change made there by another program becomes visible.
    [RelayCommand]
    private Task Refresh()
    {
        PrepareForRefresh();
        if (ViewMode == ViewMode.Tree && !IsMounted)
        {
            _ = RefreshTreeAsync();
        }

        return LoadAsync(CurrentPath);
    }

    /// <summary>Switch to an archive VFS and navigate to its root.</summary>
    public void NavigateToArchive(IVirtualFileSystem archiveFs) =>
        Mount(archiveFs, "/", kind: MountKind.Archive);

    /// <summary>
    /// Switches the panel to <paramref name="vfs"/> and navigates to <paramref name="rootPath"/>.
    /// The previous file system, path and mount context are pushed so NavigateUp can come back.
    /// </summary>
    public void Mount(IVirtualFileSystem vfs, string rootPath = "/",
        TCV.Network.ConnectionInfo? connection = null, MountKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(vfs);

        _navStack.Push(new MountFrame(_activeVfs, CurrentPath, _mountKind, RemoteConnection));
        _activeVfs = vfs;
        _mountKind = kind ?? InferKind(vfs);
        RemoteConnection = connection;
        NotifyMountChanged();
        CurrentPath = string.IsNullOrEmpty(rootPath) ? "/" : rootPath;
    }

    /// <summary>
    /// Connection behind the mounted remote file system, if any. The transfer queue needs it
    /// because it opens its own connection per transfer rather than sharing the panel's.
    /// </summary>
    public TCV.Network.ConnectionInfo? RemoteConnection { get; private set; }

    /// <summary>
    /// The server the panel's location goes through: the mounted one, or the one under an archive
    /// opened on it. What the connection chip names and Disconnect closes; null on the disk.
    /// </summary>
    public TCV.Network.ConnectionInfo? ActiveConnection =>
        RemoteConnection ?? _navStack.Select(f => f.Connection).FirstOrDefault(c => c is not null);

    /// <summary>
    /// Where the panel stands on <see cref="ActiveConnection"/>: the current path, or the folder on
    /// the server an archive was opened from. Null on the disk.
    /// </summary>
    public string? ActiveConnectionPath =>
        RemoteConnection is not null
            ? CurrentPath
            // A frame holds the state its mount replaced: the server's frame, where it stood on it.
            : _navStack.Where(f => f.Connection is not null).Select(f => f.Path).FirstOrDefault();

    /// <summary>
    /// What a mount put in front of the local disk. Callers that know say so; anything else is
    /// classified by scheme, and an unrecognised file system counts as neither archive nor
    /// remote rather than being mistaken for an FTP server.
    /// </summary>
    private static MountKind InferKind(IVirtualFileSystem vfs) => vfs.Scheme.ToLowerInvariant() switch
    {
        "ftp" or "ftps" or "sftp" => MountKind.Remote,
        "zip" or "7z" or "rar" or "tar" => MountKind.Archive,
        "search" => MountKind.SearchResults,
        _ => MountKind.Other,
    };

    /// <summary>Disposes the mounted file system and restores the previous location.</summary>
    public void Unmount()
    {
        if (!_navStack.TryPop(out var prev)) return;

        DisposeActiveVfs();
        _activeVfs = prev.Vfs;
        _mountKind = prev.Kind;
        RemoteConnection = prev.Connection;
        NotifyMountChanged();

        // Search results shown over an archive or a server stand on the same "/" as the mount under
        // them: the path does not change, so nothing would re-read the listing and ".." kept showing
        // the hits.
        var samePath = string.Equals(CurrentPath, prev.Path, StringComparison.Ordinal);
        CurrentPath = prev.Path;
        if (samePath)
        {
            _ = LoadAsync(prev.Path);
        }
    }

    /// <summary>
    /// Pops every open mount frame at once, restoring the base disk file system, and leaves
    /// <see cref="CurrentPath"/> untouched. Callers that navigate elsewhere right after avoid the
    /// throwaway directory reload each intermediate <see cref="Unmount"/> would otherwise fire —
    /// e.g. popping an archive-within-archive would reload twice before reaching the destination.
    /// </summary>
    public void UnmountAll()
    {
        if (!IsMounted) return;

        while (_activeVfs != _vfs && _navStack.TryPop(out var prev))
        {
            DisposeActiveVfs();
            _activeVfs = prev.Vfs;
            _mountKind = prev.Kind;
            RemoteConnection = prev.Connection;
        }

        NotifyMountChanged();
    }

    /// <summary>
    /// Pops every mount and shows the disk directory that was underneath them. Unlike
    /// <see cref="UnmountAll"/> the panel ends up consistent on its own: the path is a disk path
    /// again, so a caller that mounts something new right after records the right place to return to.
    /// </summary>
    public void ReturnToDisk()
    {
        if (!IsMounted) return;

        var diskPath = RootLocalPath;
        UnmountAll();
        CurrentPath = diskPath;
    }

    /// <summary>
    /// Closes the server the panel is on, with anything opened inside it (an archive read from the
    /// server), and shows what was there before connecting. Popping only the top frame left the
    /// connection open under the archive. False when the panel is on no server.
    /// </summary>
    public bool LeaveConnection()
    {
        if (ActiveConnection is null)
        {
            return false;
        }

        var returnPath = CurrentPath;
        while (ActiveConnection is not null && _navStack.TryPop(out var prev))
        {
            DisposeActiveVfs();
            _activeVfs = prev.Vfs;
            _mountKind = prev.Kind;
            RemoteConnection = prev.Connection;
            returnPath = prev.Path;
        }

        NotifyMountChanged();
        var samePath = string.Equals(CurrentPath, returnPath, StringComparison.Ordinal);
        CurrentPath = returnPath;
        if (samePath)
        {
            _ = LoadAsync(returnPath);
        }

        return true;
    }

    /// <summary>
    /// The mounts of a tab that is not on screen (ADR-0032): the whole stack, the file system on top
    /// and where in it the tab stood. The tab owns them until it is shown again, or closed: then
    /// <see cref="DisposeParked"/> closes them.
    /// </summary>
    public sealed class ParkedMounts
    {
        internal ParkedMounts(MountFrame[] frames, IVirtualFileSystem top, MountKind kind,
            TCV.Network.ConnectionInfo? topConnection, TCV.Network.ConnectionInfo? connection, string path)
        {
            Frames = frames;
            Top = top;
            Kind = kind;
            TopConnection = topConnection;
            Connection = connection;
            Path = path;
        }

        /// <summary>Bottom first: the first frame is the disk and the folder the first mount was opened from.</summary>
        internal MountFrame[] Frames { get; }

        internal IVirtualFileSystem Top { get; }

        /// <summary>The connection of the top mount itself: null for an archive, even one opened on a server.</summary>
        internal TCV.Network.ConnectionInfo? TopConnection { get; }

        public MountKind Kind { get; }

        /// <summary>The server under the tab's location, as <see cref="ActiveConnection"/> would name it.</summary>
        public TCV.Network.ConnectionInfo? Connection { get; }

        /// <summary>Where the tab stands inside the top mount.</summary>
        public string Path { get; }

        /// <summary>The disk folder under every mount: what the tab keeps as its path.</summary>
        public string RootLocalPath => Frames[0].Path;
    }

    /// <summary>
    /// Takes every mount off the panel without closing it, for the tab that is being left. The panel
    /// is back on the disk file system with its path still the mounted one: the caller navigates
    /// next. Null when nothing is mounted.
    /// </summary>
    public ParkedMounts? ParkMounts()
    {
        if (!IsMounted)
        {
            return null;
        }

        // A stack enumerates top first; the parked frames keep the order they were pushed in.
        var frames = _navStack.Reverse().ToArray();
        var parked = new ParkedMounts(frames, _activeVfs, _mountKind, RemoteConnection, ActiveConnection, CurrentPath);

        _navStack.Clear();
        _activeVfs = frames[0].Vfs;
        _mountKind = frames[0].Kind;
        RemoteConnection = frames[0].Connection;
        NotifyMountChanged();
        return parked;
    }

    /// <summary>
    /// Puts a tab's parked mounts back on the panel and shows the folder the tab was in. Whatever the
    /// panel had mounted is closed first: it belonged to no tab, the caller parks it before.
    /// </summary>
    public void RestoreMounts(ParkedMounts parked)
    {
        ArgumentNullException.ThrowIfNull(parked);
        UnmountAll();

        foreach (var frame in parked.Frames)
        {
            _navStack.Push(frame);
        }

        _activeVfs = parked.Top;
        _mountKind = parked.Kind;
        RemoteConnection = parked.TopConnection;
        NotifyMountChanged();

        // Two tabs can stand on the same path in different mounts ("/" of an archive and of a
        // server): the path alone would not change, and nothing would read the restored listing.
        var samePath = string.Equals(CurrentPath, parked.Path, StringComparison.Ordinal);
        CurrentPath = parked.Path;
        if (samePath)
        {
            _ = LoadAsync(parked.Path);
        }
    }

    /// <summary>
    /// Closes the mounts of a tab that went away while parked. A file system an operation is still
    /// streaming from is closed when that operation lets go, as on Unmount (INV-09).
    /// </summary>
    public void DisposeParked(ParkedMounts? parked)
    {
        if (parked is null)
        {
            return;
        }

        DisposeWhenIdle(parked.Top);
        foreach (var frame in parked.Frames)
        {
            DisposeWhenIdle(frame.Vfs);
        }
    }

    private void DisposeWhenIdle(IVirtualFileSystem vfs)
    {
        if (ReferenceEquals(vfs, _vfs))
        {
            return;
        }

        if (_vfsLeases.ContainsKey(vfs))
        {
            _disposeWhenIdle.Add(vfs);   // a running operation is still streaming from it
        }
        else
        {
            vfs.Dispose();
        }
    }

    /// <summary>
    /// Goes to a path typed into the address bar. A drive or UNC path is a disk location whatever
    /// the panel shows, so an open archive or connection is left first (TRAP-15); a rooted path
    /// ("/pub") on a mounted panel is looked up in the mounted file system. Existence is checked
    /// off the UI thread — the path may be on a share that takes seconds to answer (INV-06).
    /// Returns false when there is no such folder.
    /// </summary>
    public async Task<bool> TryNavigateToTypedPathAsync(string typed)
    {
        var path = typed.Trim().Trim('"');
        if (path.Length == 0) return false;

        // sftp://me@host/var/log, s3://bucket: an address of a server opens a connection to it.
        if (OpenConnectionAddress is { } open && TCV.Network.ConnectionUrl.TryParse(path, out _, out _))
        {
            return await open(path);
        }

        if (IsDiskPath(path))
        {
            if (path.Length == 2) path += "\\";   // "D:" alone means "current folder on D:"
            try { path = Path.GetFullPath(path); }   // "C:/a/../b" → "C:\b", like every other panel path
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            if (!await Task.Run(() => Directory.Exists(path))) return false;

            UnmountAll();
            CurrentPath = path;
            return true;
        }

        // Anything else is only meaningful inside a mount: on disk it would resolve against the
        // process's working directory.
        if (!IsMounted) return false;

        var vfsPath = new VfsPath(path.StartsWith('/') ? path : "/" + path);
        if (vfsPath.Value != "/" && await _activeVfs.GetEntryAsync(vfsPath) is not { IsDirectory: true })
            return false;

        CurrentPath = vfsPath.Value;
        return true;
    }

    /// <summary>
    /// Opens a connection address typed into the path bar (the shell connects; the list cannot).
    /// Null in tests, where such an address is simply not found.
    /// </summary>
    public Func<string, Task<bool>>? OpenConnectionAddress { get; set; }

    private static bool IsDiskPath(string path) =>
        (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) || path.StartsWith(@"\\");

    // Disposes the currently mounted VFS (deferring if a lease is held), or does nothing at base.
    private void DisposeActiveVfs() => DisposeWhenIdle(_activeVfs);

    /// <summary>
    /// Keeps the currently mounted file system alive for as long as the returned handle is held.
    /// Unmount still restores the panel immediately, but defers the Dispose — otherwise
    /// disconnecting or navigating up mid-copy closes the connection the copy reads from.
    /// The lease covers the active mount and the mounts beneath it (an archive opened on a server
    /// reads through that server); a mount made later is not held by it.
    /// Leases are taken and released on the UI thread.
    /// </summary>
    public IDisposable LeaseActiveVfs()
    {
        var leased = new List<IVirtualFileSystem>();
        if (!ReferenceEquals(_activeVfs, _vfs)) leased.Add(_activeVfs);
        foreach (var frame in _navStack)
        {
            if (!ReferenceEquals(frame.Vfs, _vfs)) leased.Add(frame.Vfs);
        }

        foreach (var vfs in leased)
            _vfsLeases[vfs] = _vfsLeases.GetValueOrDefault(vfs) + 1;

        return new VfsLease(this, leased);
    }

    private void ReleaseLease(IReadOnlyList<IVirtualFileSystem> leased)
    {
        foreach (var vfs in leased)
        {
            var left = _vfsLeases.GetValueOrDefault(vfs) - 1;
            if (left > 0)
            {
                _vfsLeases[vfs] = left;
                continue;
            }

            _vfsLeases.Remove(vfs);
            if (_disposeWhenIdle.Remove(vfs))
                vfs.Dispose();
        }
    }

    private sealed class VfsLease : IDisposable
    {
        private FileListViewModel? _owner;
        private readonly IReadOnlyList<IVirtualFileSystem> _leased;

        public VfsLease(FileListViewModel owner, IReadOnlyList<IVirtualFileSystem> leased)
        {
            _owner = owner;
            _leased = leased;
        }

        public void Dispose()
        {
            var owner = _owner;
            if (owner is null) return;   // leases release once
            _owner = null;
            owner.ReleaseLease(_leased);
        }
    }

    private void NotifyMountChanged()
    {
        OnPropertyChanged(nameof(IsMounted));
        OnPropertyChanged(nameof(IsInsideArchive));
        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(IsSearchResults));
        OnPropertyChanged(nameof(EntriesOnDisk));
        OnPropertyChanged(nameof(ActiveConnection));
        OnPropertyChanged(nameof(IsConnectionFailed));
        OnPropertyChanged(nameof(MountRootLabel));
        OnPropertyChanged(nameof(BreadcrumbSegments));   // disk and VFS paths split differently
        MountChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after something was mounted, unmounted, parked or restored.</summary>
    public event EventHandler? MountChanged;

    // ──────────────────────────────── Tree mode ────────────────────────────────

    public ObservableCollection<TreeNodeViewModel> TreeRoots { get; } = new();

    private TreeNodeViewModel? _selectedTreeNode;

    public bool IsBuildingTree { get; private set; }

    /// <summary>The newest tree build; an older one still waiting for the drive list gives way.</summary>
    private int _treeBuildGeneration;

    private async Task BuildTreeAsync(string currentPath)
    {
        var generation = ++_treeBuildGeneration;
        IsBuildingTree = true;
        try
        {
            TreeRoots.Clear();
            _selectedTreeNode = null;

            var drives = await FolderTreeNodes.ReadDriveRootsAsync();

            // Leaving the Tree view and coming back during that wait starts a second build; both
            // would add their drives to the roots the second one cleared, listing every drive twice.
            if (generation != _treeBuildGeneration || drives is null)
            {
                return;
            }

            FolderTreeNodes.MergeRoots(TreeRoots, drives, CreateTreeRoot);
            await ExpandTreeToPathAsync(currentPath);
        }
        finally
        {
            if (generation == _treeBuildGeneration)
            {
                IsBuildingTree = false;
            }
        }
    }

    private TreeNodeViewModel CreateTreeRoot(string name, string root) => new(name, root, true, _vfs);

    /// <summary>The newest walk to the panel's folder; an older one still loading a level gives way.</summary>
    private int _treeFollowGeneration;

    private async Task ExpandTreeToPathAsync(string path)
    {
        if (TreeRoots.Count == 0)
        {
            return;
        }

        // The tree is the disk's. An archive or server path ("/", "/home/me") has no node in it, and
        // read as a Windows path it named the current drive's root — selecting that node then pointed
        // the mounted file system at "C:\" and broke the listing.
        if (IsMounted)
        {
            return;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // Skip if this path is already selected
        if (FolderTree.SamePath(_selectedTreeNode?.FullPath, normalized))
        {
            return;
        }

        var generation = ++_treeFollowGeneration;

        // The panel's folder is opened too: in this view the tree is the file list.
        var current = await FolderTreeNodes.ExpandToAsync(TreeRoots, normalized, expandTarget: true);
        if (generation != _treeFollowGeneration)
        {
            return;
        }

        if (current is null)
        {
            // A network share, or a drive the tree does not list: no node is where the panel is.
            if (_selectedTreeNode is not null)
            {
                _selectedTreeNode.IsSelected = false;
                _selectedTreeNode = null;
            }

            return;
        }

        if (_selectedTreeNode is not null && !ReferenceEquals(_selectedTreeNode, current))
        {
            _selectedTreeNode.IsSelected = false;
        }

        // A folder the tree does not show (a hidden one) leaves nothing selected: selecting the
        // ancestor it stopped at would navigate the panel there (selection is navigation here).
        _selectedTreeNode = FolderTree.SamePath(current.FullPath, normalized) ? current : null;
        if (_selectedTreeNode is not null)
        {
            _selectedTreeNode.IsSelected = true;
        }
    }

    /// <summary>
    /// Merges a listing of a disk folder into the Tree view's node for it, if the tree shows that
    /// folder already — this panel's own loads, and the other panel's (MainViewModel passes them on).
    /// </summary>
    public void ApplyListingToTree(string path, IReadOnlyList<IFileEntry> listing)
    {
        if (TreeRoots.Count > 0)
        {
            FolderTreeNodes.FindLoaded(TreeRoots, path)?.ApplyListing(listing);
        }
    }

    /// <summary>Brings the Tree view's drive roots in line with the drives now present (a USB stick came or went).</summary>
    public async Task RefreshTreeRootsAsync()
    {
        if (TreeRoots.Count == 0 || IsBuildingTree)
        {
            return;   // never built, or being built: that build reads the drives itself
        }

        var drives = await FolderTreeNodes.ReadDriveRootsAsync();
        if (drives is not null && TreeRoots.Count > 0 && !IsBuildingTree)
        {
            FolderTreeNodes.MergeRoots(TreeRoots, drives, CreateTreeRoot);
        }
    }

    /// <summary>Re-reads the drives and every open folder of the Tree view (a manual refresh).</summary>
    private async Task RefreshTreeAsync()
    {
        await RefreshTreeRootsAsync();
        foreach (var root in TreeRoots.ToList())
        {
            await root.RefreshOpenSubtreeAsync();
        }
    }

    public void ApplySettings(PanelSettings ps)
    {
        ShowHiddenFiles = ps.ShowHiddenFiles;
        SortBy = ps.SortBy;
        SortDescending = ps.SortDescending;
        FoldersFirst = ps.FoldersFirst;
        ViewMode = ps.ViewMode;
        ShowExtensions = ps.ShowExtensions;
        HideIgnored = ps.HideIgnored;
        Columns = ColumnCatalog.Normalize(ps.Columns);
    }

    /// <summary>Writes the current column layout back into the settings object before it is saved.</summary>
    public void SaveColumns(PanelSettings ps)
    {
        // Widths live on the grid until someone asks for them; without this the layout persisted
        // here would still hold the defaults, whatever the user dragged the dividers to.
        ColumnWidthsRequested?.Invoke(this, EventArgs.Empty);
        ps.Columns = _columns.ToList();
    }

    /// <summary>
    /// Applies a saved view state in one shot without firing the per-property re-sort / re-filter /
    /// tree-rebuild reactions. The caller is expected to (re)load the directory afterwards — that load
    /// sorts and filters the fresh listing in a single pass — and to call <see cref="RebuildTreeIfNeeded"/>
    /// once the new path is set. Used when restoring a workspace, where reacting to each setter would
    /// churn the outgoing listing that is about to be replaced.
    /// </summary>
    public void ApplyViewState(
        ViewMode viewMode, SortColumn sortBy, bool sortDescending,
        bool foldersFirst, bool showHiddenFiles, bool showExtensions)
    {
        _suppressViewStateReactions = true;
        try
        {
            ViewMode = viewMode;
            SortBy = sortBy;
            SortDescending = sortDescending;
            FoldersFirst = foldersFirst;
            ShowHiddenFiles = showHiddenFiles;
            ShowExtensions = showExtensions;
        }
        finally
        {
            _suppressViewStateReactions = false;
        }
    }

    /// <summary>Rebuilds the folder tree for the current path. Used after a workspace restore, where
    /// the tree build is suppressed while <see cref="ApplyViewState"/> sets the mode in bulk.</summary>
    public void RebuildTreeIfNeeded()
    {
        if (ViewMode == ViewMode.Tree && !string.IsNullOrEmpty(CurrentPath))
            _ = BuildTreeAsync(CurrentPath);
    }
}
