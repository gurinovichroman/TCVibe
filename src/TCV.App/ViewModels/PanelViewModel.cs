using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels;

/// <summary>What the connection chip on a panel's drive strip shows.</summary>
public enum ConnectionChipState
{
    /// <summary>No connection: the chip is the button that opens one.</summary>
    None,
    Connecting,

    /// <summary>Waiting for the user to sign in in the browser.</summary>
    SigningIn,

    /// <summary>The last listing came back.</summary>
    Online,

    /// <summary>A listing is on its way.</summary>
    Busy,

    /// <summary>The last listing failed: the server dropped or refused.</summary>
    Failed,

    /// <summary>The connection dropped and is being opened again by itself (6.82).</summary>
    Reconnecting,
}

public partial class PanelViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly FileWatcher _fileWatcher;

    private readonly Stack<string> _backStack = new();
    private readonly Stack<string> _forwardStack = new();
    private bool _suppressHistoryPush;
    private bool _syncingFromFileList;
    private string _previousPath = string.Empty;

    /// <summary>
    /// The file system the back/forward stacks were recorded against. History does not cross a
    /// mount boundary: an archive path ("/sub") replayed on the local disk, or a disk path replayed
    /// against an archive, points nowhere, so entering or leaving a mount starts a fresh history.
    /// </summary>
    private TCV.Core.Interfaces.IVirtualFileSystem? _historyVfs;

    public PanelViewModel(
        string name,
        FileListViewModel fileList,
        DriveBarViewModel driveBar,
        StatusBarViewModel statusBar,
        AppSettings settings,
        FileWatcher fileWatcher)
    {
        Name = name;
        FileList = fileList;
        DriveBar = driveBar;
        StatusBar = statusBar;
        _settings = settings;
        _fileWatcher = fileWatcher;

        FileList.ColorRules = settings.ColorRules;
        FileList.QuickSearchJumps = settings.QuickSearchJumps;

        // An agent writing into the folder must not keep throwing the cursor back to "..", nor close
        // a name being typed in the list: that reload waits for the edit to end.
        _fileWatcher.Changed += (_, _) =>
            System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (FileList.PostponeReloadForNameEdit())
                {
                    return;
                }

                FileList.PrepareForRefresh();
                _ = FileList.LoadAsync(CurrentPath);
            });

        DriveBar.DriveSelected += (_, drive) => OnDriveChosen(drive);
        // A drive that came or went is a root that comes or goes in the Tree view as well.
        DriveBar.DrivesRefreshed += (_, _) => _ = FileList.RefreshTreeRootsAsync();

        // SelectedFiles is a get-only collection and never raises PropertyChanged for itself —
        // the status bar has to follow the collection and each directory load instead.
        FileList.SelectedFiles.CollectionChanged += (_, _) => UpdateStatusBar(recountListing: false);
        // A marked folder measured after it was marked (Space) adds its size to the selection's total.
        FileList.FolderSizesChanged += (_, _) => UpdateStatusBar(recountListing: false);
        // The folder summary follows what is shown (quick filter, hidden files); a load also
        // re-measures free space, which a copy or delete on the same drive has moved.
        FileList.Files.CollectionChanged += (_, _) => UpdateStatusBar();
        FileList.DirectoryLoaded += (_, _) => UpdateStatusBar(refreshFreeSpace: true);

        // The tab on screen follows what the panel shows: a server, an archive, the disk.
        FileList.MountChanged += (_, _) =>
        {
            RefreshActiveTabLocation();
            RefreshConnectionChip();
        };

        // The chip's dot follows the listing: busy while it loads, red when the server stopped answering.
        FileList.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileListViewModel.IsLoading) or nameof(FileListViewModel.LoadError)
                or nameof(FileListViewModel.ReconnectStatus))
            {
                RefreshConnectionChip();
            }
        };

        FileList.PropertyChanged += (_, e) =>
        {
            // Sync panel path when FileList navigates internally (e.g. NavigateUp)
            if (e.PropertyName == nameof(FileListViewModel.CurrentPath)
                && !_syncingFromFileList
                && FileList.CurrentPath != CurrentPath)
            {
                _syncingFromFileList = true;
                CurrentPath = FileList.CurrentPath;
                _syncingFromFileList = false;
            }
        };

        // One hook instead of a RefreshBranch() call at the end of every tab command: the branch has
        // to follow both where the panel is and which tab is showing it, and both of those already
        // announce themselves. Switching between two tabs on the same folder changes only the
        // latter, so watching CurrentPath alone would leave the new tab unlabelled.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CurrentPath) or nameof(ActiveTab)) RefreshBranch();
            if (e.PropertyName is nameof(ActiveTab))
            {
                RefreshInactiveBranches();
            }
        };

        // Tabs that are not showing keep their own label, so a restored workspace or a freshly
        // duplicated tab is not blank until it is visited.
        Tabs.CollectionChanged += (_, _) => RefreshInactiveBranches();
    }

    /// <summary>
    /// Re-reads the active tab's branch. The read is a small file (<c>.git/HEAD</c>) but goes through
    /// the thread pool anyway — the panel may be sitting on a network share, where any disk touch on
    /// the UI thread is a freeze (INV-06).
    /// </summary>
    public async void RefreshBranch()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        // A mounted panel shows VFS paths ("/pub"): there is no repository under them, and asking
        // would only walk the disk looking for one.
        if (FileList.IsMounted)
        {
            tab.Branch = string.Empty;
            return;
        }

        var path = CurrentPath;
        var branch = await Task.Run(() => TCV.Git.GitHeadReader.ReadBranch(path));

        // Navigation continues while the read is in flight; labelling the new folder with the old
        // folder's branch would be worse than labelling nothing.
        if (ReferenceEquals(ActiveTab, tab) && CurrentPath == path)
            tab.Branch = branch;
    }

    /// <summary>
    /// Reads the branch for every tab that is not the active one. The active tab follows the panel
    /// (<see cref="RefreshBranch"/>); the others sit on the folder they were left in, so their
    /// label only changes when the tab set or the active tab does.
    /// </summary>
    private async void RefreshInactiveBranches()
    {
        foreach (var tab in Tabs.ToList())
        {
            // A tab parked on a server or in an archive is labelled by that, not by the disk under it.
            if (tab.IsActive || tab.Location is not null)
            {
                continue;
            }

            var path = tab.Path;
            var branch = await Task.Run(() => TCV.Git.GitHeadReader.ReadBranch(path));
            if (!tab.IsActive && tab.Path == path)
            {
                tab.Branch = branch;
            }
        }
    }

    private void UpdateStatusBar(bool refreshFreeSpace = false, bool recountListing = true) =>
        StatusBar.Update(FileList.SelectedFiles, FileList.Files, CurrentPath, refreshFreeSpace, recountListing);

    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveTab))]
    private bool _isActive;

    [ObservableProperty]
    private string _currentPath = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateBackCommand))]
    private bool _canGoBack;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigateForwardCommand))]
    private bool _canGoForward;

    /// <summary>
    /// The drive combo was changed by the user: go to that drive (leaving any mount). The combo
    /// also follows navigation (<see cref="DriveBarViewModel.SelectDriveForPath"/>), which lands
    /// here with the drive already current — that must not navigate again.
    /// </summary>
    private void OnDriveChosen(string drive)
    {
        var current = FileList.IsMounted ? FileList.RootLocalPath : CurrentPath;
        if (current.StartsWith(drive, StringComparison.OrdinalIgnoreCase)
            || string.Equals(current.TrimEnd('\\') + "\\", drive, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FileList.UnmountAll();
        CurrentPath = drive;
    }

    public FileListViewModel FileList { get; }
    public DriveBarViewModel DriveBar { get; }
    public StatusBarViewModel StatusBar { get; }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public TabViewModel? ActiveTab => Tabs.FirstOrDefault(t => t.IsActive);

    partial void OnIsActiveChanged(bool value)
    {
        FileList.IsActive = value;
        OnPropertyChanged(nameof(BorderBrush));
    }

    /// <summary>Fallbacks for when no theme is loaded (tests); the running app reads the theme's brushes.</summary>
    private static readonly SolidColorBrush FallbackActiveBrush =
        new(Color.FromRgb(0x00, 0x7A, 0xCC));

    private static readonly SolidColorBrush FallbackInactiveBrush =
        new(Color.FromRgb(0x55, 0x55, 0x55));

    /// <summary>
    /// A theme brush by key. The frame is re-read on every focus change and on
    /// <see cref="RefreshFrame"/>, which a theme or accent change calls.
    /// </summary>
    private static Brush ThemeBrush(string key, Brush fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? fallback;

    /// <summary>Re-reads the frame brush after the theme or the accent colour changed.</summary>
    public void RefreshFrame() => OnPropertyChanged(nameof(BorderBrush));

    private SolidColorBrush? _accentBrush;

    /// <summary>
    /// The active workspace's colour, pushed down by <c>MainViewModel</c> on every switch. It only
    /// shows on the focused panel: painting both would say nothing about where the keyboard is.
    /// </summary>
    public SolidColorBrush? AccentBrush
    {
        get => _accentBrush;
        set
        {
            if (ReferenceEquals(_accentBrush, value)) return;
            _accentBrush = value;
            OnPropertyChanged(nameof(BorderBrush));
        }
    }

    /// <summary>Panel frame: workspace accent when focused, otherwise the neutral edge.</summary>
    public Brush BorderBrush =>
        IsActive
            ? AccentBrush ?? ThemeBrush("AccentBrush", FallbackActiveBrush)
            : ThemeBrush("BorderBrush", FallbackInactiveBrush);

    partial void OnCurrentPathChanged(string value)
    {
        // Entering or leaving a mount (or moving to another one) invalidates what was recorded.
        if (!ReferenceEquals(_historyVfs, FileList.ActiveVfs))
        {
            _historyVfs = FileList.ActiveVfs;
            ClearHistory();
        }

        if (!_suppressHistoryPush && !string.IsNullOrEmpty(_previousPath) && _previousPath != value)
        {
            _backStack.Push(_previousPath);
            _forwardStack.Clear();
            CanGoBack = _backStack.Count > 0;
            CanGoForward = false;
        }
        _previousPath = value;

        _syncingFromFileList = true;
        FileList.CurrentPath = value;
        _syncingFromFileList = false;

        // Below this point everything is about the local disk. While a mount is open the panel
        // shows VFS paths like "/pub", so the disk-side state follows the directory underneath
        // the mount instead of being handed a path that does not exist on any drive.
        var mounted = FileList.IsMounted;
        DriveBar.SelectDriveForPath(mounted ? FileList.RootLocalPath : value);
        UpdateStatusBar();   // free space follows the drive, not the listing
        RefreshActiveTabLocation();
        RefreshConnectionChip();   // a Windows share is a path, not a mount

        // A mounted path is not a directory FileSystemWatcher can observe. Stop watching the
        // last local directory as well: its disk events would otherwise keep reloading the
        // mounted listing, re-issuing FTP round trips and resetting selection.
        if (mounted)
        {
            _fileWatcher.Stop();
            return;
        }

        _fileWatcher.Watch(value);

        // Sync active tab path (only if not locked)
        if (ActiveTab is { IsLocked: false } tab)
            tab.Path = value;
    }

    /// <summary>
    /// The path a tab should remember: a mount is gone by the next launch, so tabs keep the
    /// disk directory underneath it rather than "/pub" or an archive-internal path.
    /// </summary>
    private string TabPath => FileList.IsMounted ? FileList.RootLocalPath : CurrentPath;

    /// <summary>Set while tabs are being switched: the location is refreshed once, at the end.</summary>
    private bool _switchingTabs;

    /// <summary>Re-describes the tab on screen: a server, an archive, search results, or nothing for the disk.</summary>
    private void RefreshActiveTabLocation()
    {
        if (_switchingTabs || ActiveTab is not { } tab)
        {
            return;
        }

        tab.Location = DescribeLocation(FileList);
    }

    /// <summary>
    /// What a tab on this panel's location is titled and how its address reads. A server is named by
    /// its connection, the folder on it goes beside the title; an archive by its file name.
    /// </summary>
    public static TabLocation? DescribeLocation(FileListViewModel fileList)
    {
        if (!fileList.IsMounted)
        {
            return null;
        }

        var path = fileList.CurrentPath;
        var folder = string.IsNullOrEmpty(path) || path == "/"
            ? null
            : System.IO.Path.GetFileName(path.TrimEnd('/'));
        var kind = fileList.IsInsideArchive ? MountKind.Archive
            : fileList.IsRemote ? MountKind.Remote
            : fileList.IsSearchResults ? MountKind.SearchResults
            : MountKind.Other;

        if (fileList.ActiveConnection is { } connection)
        {
            // An archive opened on the server: still the server's tab, the archive goes beside it.
            var detail = kind == MountKind.Archive ? folder ?? fileList.MountRootLabel : folder;
            var serverPath = fileList.ActiveConnectionPath ?? path;
            return new TabLocation(kind, ConnectionTitle(connection), detail,
                connection.DescribeAddress(serverPath), connection);
        }

        var root = fileList.MountRootLabel;
        return new TabLocation(kind, root, folder, folder is null ? root : $"{root.TrimEnd('/')}/{path.TrimStart('/')}", null);
    }

    /// <summary>The name a connection goes by on its tab and chip: its saved name, else its host.</summary>
    public static string ConnectionTitle(TCV.Network.ConnectionInfo connection) =>
        !string.IsNullOrWhiteSpace(connection.Name) ? connection.Name
        : !string.IsNullOrWhiteSpace(connection.Host) ? connection.Host
        : connection.DescribeAddress("/");

    /// <summary>
    /// Opens a second connection to a server in the tab on screen — what duplicating a tab on a
    /// server does. Set by the shell; null in tests, where the copy shows the disk folder instead.
    /// </summary>
    public Func<TCV.Network.ConnectionInfo, string, Task>? OpenConnectionInActiveTab { get; set; }

    /// <summary>The connections open in this panel's tabs: the one on screen and the parked ones.</summary>
    public IEnumerable<(TabViewModel Tab, TCV.Network.ConnectionInfo Connection)> OpenConnections()
    {
        foreach (var tab in Tabs)
        {
            var connection = tab.IsActive ? FileList.ActiveConnection : tab.Parked?.Connection;
            if (connection is not null)
            {
                yield return (tab, connection);
            }
        }
    }

    /// <summary>
    /// Shows <paramref name="tab"/>, parking what the panel had mounted in the tab being left
    /// (ADR-0032): its server, archive or search results wait there, open, until it is shown again.
    /// A tab that is no longer in the list (it was just closed) parks nothing — its mounts are closed.
    /// </summary>
    private void ActivateTab(TabViewModel tab, TabViewModel? leaving, bool keepHistory = false)
    {
        _switchingTabs = true;
        try
        {
            foreach (var t in Tabs)
            {
                t.IsActive = ReferenceEquals(t, tab);
            }

            if (leaving is not null && !ReferenceEquals(leaving, tab) && Tabs.Contains(leaving))
            {
                leaving.Parked = FileList.ParkMounts();
            }
            else if (!ReferenceEquals(leaving, tab))
            {
                FileList.UnmountAll();
            }

            // Back/Forward belong to the tab that recorded them: replayed in another tab they would
            // walk that tab into folders it never visited (and drag its unlocked path along).
            if (!keepHistory)
            {
                ClearHistory();
            }

            _suppressHistoryPush = true;
            if (tab.Parked is { } parked)
            {
                tab.Parked = null;
                FileList.RestoreMounts(parked);
            }
            else if (!ReferenceEquals(leaving, tab) || !FileList.IsMounted)
            {
                CurrentPath = tab.Path;
            }

            _suppressHistoryPush = false;
        }
        finally
        {
            _switchingTabs = false;
            _suppressHistoryPush = false;
        }

        RefreshActiveTabLocation();
        RefreshConnectionChip();
        OnPropertyChanged(nameof(ActiveTab));
    }

    /// <summary>
    /// Closes a connection in every tab of this panel that has it — the connection manager's
    /// Disconnect. A parked tab goes back to the disk folder it was opened from.
    /// </summary>
    public void CloseConnection(Guid id)
    {
        foreach (var tab in Tabs)
        {
            if (tab.IsActive)
            {
                if (FileList.ActiveConnection?.Id == id)
                {
                    FileList.LeaveConnection();
                }
            }
            else if (tab.Parked?.Connection?.Id == id)
            {
                FileList.DisposeParked(tab.Parked);
                tab.Parked = null;
                tab.Location = null;
            }
        }
    }

    // ── Connection chip (drive strip) ─────────────────────────────────────────

    /// <summary>The connection being opened in this panel, while it is: the chip says so and offers to cancel.</summary>
    [ObservableProperty]
    private TCV.Network.ConnectionInfo? _connectingTo;

    /// <summary>The connect is waiting for the user to sign in in the browser.</summary>
    [ObservableProperty]
    private bool _isSigningIn;

    partial void OnConnectingToChanged(TCV.Network.ConnectionInfo? value) => RefreshConnectionChip();

    partial void OnIsSigningInChanged(bool value) => RefreshConnectionChip();

    /// <summary>
    /// The saved Windows share a disk path lies in, if this window opened it. A share is no mount —
    /// the panel reads it as a disk path — so the shell, which opened it, answers. Null in tests.
    /// </summary>
    public Func<string, TCV.Network.ConnectionInfo?>? ShareConnectionFor { get; set; }

    /// <summary>The connection the chip names: the one being opened, the server under the tab, or the share it stands in.</summary>
    public TCV.Network.ConnectionInfo? ChipConnection =>
        ConnectingTo ?? FileList.ActiveConnection ?? (FileList.IsMounted ? null : ShareConnectionFor?.Invoke(CurrentPath));

    public ConnectionChipState ChipState =>
        ConnectingTo is not null ? (IsSigningIn ? ConnectionChipState.SigningIn : ConnectionChipState.Connecting)
        : ChipConnection is null ? ConnectionChipState.None
        : FileList.IsReconnecting ? ConnectionChipState.Reconnecting
        : FileList.HasLoadError ? ConnectionChipState.Failed
        : FileList.IsLoading ? ConnectionChipState.Busy
        : ConnectionChipState.Online;

    /// <summary>The tab is on a connection (not merely opening one): Disconnect shows beside the chip.</summary>
    public bool HasConnection => ConnectingTo is null && ChipConnection is not null;

    public bool IsConnecting => ConnectingTo is not null;

    /// <summary>The tab's connection dropped and comes back by itself: the chip offers to stop it.</summary>
    public bool IsReconnecting => ConnectingTo is null && FileList.IsReconnecting;

    /// <summary>The chip's text: the connection's name, or the button's own label when there is none.</summary>
    public string ChipTitle => ChipConnection is { } connection
        ? ConnectionTitle(connection)
        : Localization.LocalizationManager.Get("Drive_ServersLabel");

    /// <summary>The chip's icon: a server, a cloud (object storage, a drive), a Windows share.</summary>
    public string ChipKind => ChipConnection switch
    {
        { IsObjectStorage: true } or { IsOAuth: true } => "cloud",
        { IsSmb: true } => "share",
        _ => "server",
    };

    /// <summary>The colour tag of the chip's connection, or null.</summary>
    public System.Windows.Media.Brush? ChipColor => ActiveTab?.ConnectionColor;

    /// <summary>
    /// Which connection, how it is reached and what state it is in: the user's complaint was not
    /// knowing which server the panel was on, nor whether it was still connected.
    /// </summary>
    public string ChipToolTip
    {
        get
        {
            if (ChipConnection is not { } connection)
            {
                return Localization.LocalizationManager.Get("Drive_ServersTip");
            }

            var path = ConnectingTo is null
                ? FileList.ActiveConnectionPath ?? (FileList.IsMounted ? "/" : CurrentPath)
                : connection.RemotePath;
            var address = connection.IsSmb && ConnectingTo is null ? CurrentPath : connection.DescribeAddress(path);
            var state = ChipState switch
            {
                ConnectionChipState.Connecting => Localization.LocalizationManager.Get("Chip_Connecting"),
                ConnectionChipState.SigningIn => Localization.LocalizationManager.Get("Chip_SigningIn"),
                ConnectionChipState.Busy => Localization.LocalizationManager.Get("Chip_Busy"),
                ConnectionChipState.Reconnecting => FileList.ReconnectStatus ?? string.Empty,
                ConnectionChipState.Failed => string.Format(
                    Localization.LocalizationManager.Get("Chip_Failed"), FileList.LoadError),
                _ => Localization.LocalizationManager.Get("Chip_Online"),
            };
            if (connection.ReadOnly)
            {
                state += " · " + Localization.LocalizationManager.Get("Chip_ReadOnly");
            }

            return string.Join(Environment.NewLine,
                $"{ConnectionTitle(connection)} · {Localization.LocalizationManager.Get("Conn_Protocol_" + connection.Protocol)}",
                address,
                state);
        }
    }

    /// <summary>Re-reads everything the connection chip shows.</summary>
    public void RefreshConnectionChip()
    {
        OnPropertyChanged(nameof(ChipConnection));
        OnPropertyChanged(nameof(ChipState));
        OnPropertyChanged(nameof(HasConnection));
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ChipTitle));
        OnPropertyChanged(nameof(ChipKind));
        OnPropertyChanged(nameof(ChipColor));
        OnPropertyChanged(nameof(ChipToolTip));
    }

    /// <summary>
    /// Every file system this panel holds open: the ones on screen and the ones background tabs keep
    /// (ADR-0032) — what the keep-alive pings (6.82). The disk is not one of them.
    /// </summary>
    public IReadOnlyList<TCV.Core.Interfaces.IVirtualFileSystem> OpenFileSystems()
    {
        var open = new List<TCV.Core.Interfaces.IVirtualFileSystem>(FileList.MountedFileSystems());
        foreach (var tab in Tabs)
        {
            if (!tab.IsActive && tab.Parked is { } parked)
            {
                open.AddRange(FileList.MountedFileSystems(parked));
            }
        }

        return open;
    }

    /// <summary>Closes every mount the tabs keep, the one on screen included: the window is going away.</summary>
    public void CloseAllMounts()
    {
        foreach (var tab in Tabs)
        {
            FileList.DisposeParked(tab.Parked);
            tab.Parked = null;
        }

        FileList.UnmountAll();
    }

    public void LoadInitialPath(string path)
    {
        if (!Tabs.Any())
        {
            // Restore tabs saved from last session
            var panelSettings = Name == "Left" ? _settings.LeftPanel : _settings.RightPanel;
            if (panelSettings.Tabs.Any())
            {
                foreach (var state in panelSettings.Tabs)
                {
                    var tab = new TabViewModel(state.Path ?? string.Empty, state.CustomTitle)
                    {
                        IsLocked = state.IsLocked
                    };
                    Tabs.Add(tab);
                }
                Tabs[Math.Clamp(panelSettings.ActiveTabIndex, 0, Tabs.Count - 1)].IsActive = true;
            }
            else
            {
                var tab = new TabViewModel(path) { IsActive = true };
                Tabs.Add(tab);
            }
        }

        _suppressHistoryPush = true;
        CurrentPath = ActiveTab?.Path ?? path;
        _suppressHistoryPush = false;
    }

    /// <summary>Forgets Back/Forward. <c>_previousPath</c> goes too, or the next move would push it.</summary>
    private void ClearHistory()
    {
        _backStack.Clear();
        _forwardStack.Clear();
        _previousPath = string.Empty;
        CanGoBack = false;
        CanGoForward = false;
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void NavigateBack()
    {
        if (!_backStack.TryPop(out var prev)) return;
        _forwardStack.Push(CurrentPath);
        _suppressHistoryPush = true;
        CurrentPath = prev;
        _suppressHistoryPush = false;
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NavigateForward()
    {
        if (!_forwardStack.TryPop(out var next)) return;
        _backStack.Push(CurrentPath);
        _suppressHistoryPush = true;
        CurrentPath = next;
        _suppressHistoryPush = false;
        CanGoBack = _backStack.Count > 0;
        CanGoForward = _forwardStack.Count > 0;
    }

    [RelayCommand]
    private void NewTab()
    {
        var tab = new TabViewModel(TabPath);

        // On a server or in an archive the new tab opens on the disk folder underneath, and the
        // mount stays with the tab it was opened in (ADR-0032).
        if (FileList.IsMounted)
        {
            var leaving = ActiveTab;
            Tabs.Add(tab);
            ActivateTab(tab, leaving);
            return;
        }

        foreach (var t in Tabs) t.IsActive = false;
        tab.IsActive = true;
        Tabs.Add(tab);
        OnPropertyChanged(nameof(ActiveTab));
    }

    [RelayCommand]
    private void CloseTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        // A locked tab is one the user pinned on purpose: the close button is hidden for it, so the
        // keyboard path (Ctrl+W) must not be the way it disappears by accident.
        if (tab is null || tab.IsLocked || Tabs.Count <= 1)
        {
            return;
        }

        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        // Closing a tab closes what it keeps open: its server connection goes with it.
        if (tab.IsActive)
        {
            ActivateTab(Tabs[Math.Min(idx, Tabs.Count - 1)], leaving: tab);
        }
        else
        {
            FileList.DisposeParked(tab.Parked);
            tab.Parked = null;
        }
    }

    [RelayCommand]
    private void SwitchTab(TabViewModel tab)
    {
        // Clicking the tab that is already showing must not throw the panel out of its archive.
        if (tab.IsActive && FileList.IsMounted)
        {
            return;
        }

        ActivateTab(tab, ActiveTab, keepHistory: tab.IsActive);
    }

    [RelayCommand]
    private void NextTab()
    {
        if (Tabs.Count <= 1) return;
        var idx = ActiveTab is { } t ? Tabs.IndexOf(t) : -1;
        SwitchTab(Tabs[(idx + 1) % Tabs.Count]);
    }

    [RelayCommand]
    private void PreviousTab()
    {
        if (Tabs.Count <= 1) return;
        var idx = ActiveTab is { } t ? Tabs.IndexOf(t) : 0;
        SwitchTab(Tabs[(idx - 1 + Tabs.Count) % Tabs.Count]);
    }

    [RelayCommand]
    private void DuplicateTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;

        // A tab on a server or in an archive keeps its mount (ADR-0032); the copy starts on the disk
        // folder underneath, and on a server opens a connection of its own to the same place.
        var leaving = ActiveTab;
        var server = tab.IsActive ? FileList.ActiveConnection : tab.Parked?.Connection;
        var serverPath = tab.IsActive ? FileList.ActiveConnectionPath : tab.Parked is { Kind: MountKind.Remote } p ? p.Path : null;
        var diskPath = tab.IsActive ? TabPath : tab.Parked?.RootLocalPath ?? tab.Path;

        // The tab on screen, on the disk: the copy shows the same folder, nothing to reload.
        if (tab.IsActive && !FileList.IsMounted
            && string.Equals(tab.Path, CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            var copy = new TabViewModel(TabPath, tab.CustomTitle);
            foreach (var t in Tabs)
            {
                t.IsActive = false;
            }

            copy.IsActive = true;
            Tabs.Insert(Tabs.IndexOf(tab) + 1, copy);
            OnPropertyChanged(nameof(ActiveTab));
            return;
        }

        var newTab = new TabViewModel(diskPath, tab.CustomTitle);
        Tabs.Insert(Tabs.IndexOf(tab) + 1, newTab);
        ActivateTab(newTab, leaving);

        if (server is not null && OpenConnectionInActiveTab is { } open)
        {
            _ = open(server, serverPath ?? server.RemotePath);
        }
    }

    /// <summary>Closes every tab except <paramref name="tab"/> — and except locked tabs, which stay.</summary>
    [RelayCommand]
    private void CloseOtherTabs(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;

        var wasActive = tab.IsActive;
        var leaving = ActiveTab;
        foreach (var t in Tabs.Where(x => x != tab && !x.IsLocked).ToList())
        {
            Tabs.Remove(t);
            FileList.DisposeParked(t.Parked);
            t.Parked = null;
        }

        // The tab that stays was already showing: the panel keeps its folder (and its mount).
        if (wasActive)
        {
            OnPropertyChanged(nameof(ActiveTab));
        }
        else
        {
            ActivateTab(tab, leaving);
        }
    }

    [RelayCommand]
    private void ToggleLockTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        tab.IsLocked = !tab.IsLocked;
        if (!tab.IsLocked)
        {
            // When unlocking, sync tab path with current panel path
            tab.Path = TabPath;
        }
    }

    [RelayCommand]
    private void StartRenameTab(TabViewModel? tab)
    {
        tab ??= ActiveTab;
        if (tab is null) return;
        tab.IsRenaming = true;
    }

    public void MoveTab(TabViewModel tab, int targetIndex)
    {
        var currentIndex = Tabs.IndexOf(tab);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Tabs.Count || currentIndex == targetIndex) return;
        Tabs.Move(currentIndex, targetIndex);
    }

    public PanelSettings SaveState()
    {
        var ps = Name == "Left" ? _settings.LeftPanel : _settings.RightPanel;
        // Persist the disk location under any open archive/connection — that path still
        // resolves on the next launch, "/pub" or "/folder/in.zip" would not
        ps.LastPath = FileList.RootLocalPath;
        ps.SortBy = FileList.SortBy;
        ps.SortDescending = FileList.SortDescending;
        ps.ShowHiddenFiles = FileList.ShowHiddenFiles;
        ps.FoldersFirst = FileList.FoldersFirst;
        ps.ViewMode = FileList.ViewMode;
        ps.ShowExtensions = FileList.ShowExtensions;
        ps.HideIgnored = FileList.HideIgnored;
        FileList.SaveColumns(ps);
        ps.Tabs = Tabs.Select(t => new TabState
        {
            Path = t.Path,
            CustomTitle = t.CustomTitle,
            IsLocked = t.IsLocked,
        }).ToList();
        ps.ActiveTabIndex = ActiveTab is { } active ? Math.Max(0, Tabs.IndexOf(active)) : 0;
        return ps;
    }

    /// <summary>Captures this panel's full state (all tabs + active tab + view state) for a workspace.</summary>
    public WorkspacePanelState CaptureSnapshot()
    {
        var activeIndex = ActiveTab is { } a ? Math.Max(0, Tabs.IndexOf(a)) : 0;
        var tabs = Tabs.Select(t => new TabState
        {
            Path = t.Path,
            CustomTitle = t.CustomTitle,
            IsLocked = t.IsLocked,
        }).ToList();

        // A mounted active tab (archive/FTP) holds a VFS path like "/pub" that cannot be reopened
        // next session — persist the disk directory underneath the mount instead, as SaveState does.
        if (FileList.IsMounted && activeIndex < tabs.Count)
            tabs[activeIndex].Path = FileList.RootLocalPath;

        return new WorkspacePanelState
        {
            Tabs = tabs,
            ActiveTabIndex = activeIndex,
            ViewMode = FileList.ViewMode,
            SortBy = FileList.SortBy,
            SortDescending = FileList.SortDescending,
            ShowHiddenFiles = FileList.ShowHiddenFiles,
            FoldersFirst = FileList.FoldersFirst,
            ShowExtensions = FileList.ShowExtensions,
        };
    }

    /// <summary>
    /// Restores a workspace snapshot: pops any mount, rebuilds the tab set, applies the view state
    /// and navigates to the active tab. Mount state (archive/FTP) is intentionally not restored.
    /// </summary>
    public void RestoreSnapshot(WorkspacePanelState state)
    {
        // A workspace restores disk locations; every tab's archive or connection is closed first
        // (TRAP-15), the parked ones included.
        CloseAllMounts();

        // A restored workspace starts with a clean navigation history: Back/Forward must not
        // walk into the directories of the workspace we just switched away from.
        ClearHistory();
        _historyVfs = FileList.ActiveVfs;

        Tabs.Clear();
        if (state.Tabs.Count == 0)
        {
            Tabs.Add(new TabViewModel(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        }
        else
        {
            foreach (var ts in state.Tabs)
                Tabs.Add(new TabViewModel(ts.Path ?? string.Empty, ts.CustomTitle) { IsLocked = ts.IsLocked });
        }

        var index = Math.Clamp(state.ActiveTabIndex, 0, Tabs.Count - 1);
        for (var i = 0; i < Tabs.Count; i++)
            Tabs[i].IsActive = i == index;

        // Apply the view state in bulk without reacting to each setter. Reacting now would sort,
        // filter and (for tree mode) walk the outgoing workspace's listing — work thrown away
        // milliseconds later. The CurrentPath change below reloads and sorts/filters the fresh
        // listing in one pass; the tree is then rebuilt for the new path.
        FileList.ApplyViewState(state.ViewMode, state.SortBy, state.SortDescending,
            state.FoldersFirst, state.ShowHiddenFiles, state.ShowExtensions);

        _suppressHistoryPush = true;
        CurrentPath = Tabs[index].Path;
        _suppressHistoryPush = false;
        FileList.RebuildTreeIfNeeded();
        OnPropertyChanged(nameof(ActiveTab));
    }
}
