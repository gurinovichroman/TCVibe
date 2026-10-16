using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Commands;
using TCV.App.FileSystem;
using TCV.App.FileSystem.Native;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Interfaces;
using TCV.App.PluginHost;
using TCV.App.Services;
using TCV.App.Settings;
using System.Collections.ObjectModel;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
using TCV.Core.PluginSdk;
using TCV.Core.Domain;
using TCV.Network;
using TCV.Tools;
using TCV.Archives;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels;

/// <summary>
/// The window's shell. After task 6.26 it owns only what every command needs and no single command
/// group can own: the two panels and which of them is active, the selection, the guard that rejects
/// disk-only commands on a mounted panel, the file-system lease, the conflict strategy and the
/// progress teardown, plus session save/restore.
/// </summary>
/// <remarks>
/// The commands themselves live in four facades — <see cref="Files"/>, <see cref="Mounts"/>,
/// <see cref="Workspaces"/>, <see cref="Services"/> — reached through the properties below, which
/// is also how XAML binds them (<c>{Binding Files.CopyCommand}</c>) and how
/// <c>MainWindow.TryExecuteCommand</c> dispatches hotkeys. The halves that still sit in
/// <c>MainViewModel.{Git,Agents,View,Navigation,Operations,Gadgets,Remote}.cs</c> are parts of this
/// class rather than facades: each is a thin layer over one already-aggregated service
/// (<see cref="GitWorkflowServices"/>, <see cref="AgentServices"/>, …) and reads the shell's
/// plumbing directly.
/// </remarks>
public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILogger<CopyOperation> _copyLogger;
    private readonly ILogger<MoveOperation> _moveLogger;
    private readonly ILogger<DeleteOperation> _deleteLogger;
    private readonly ILogger<CreateDirectoryOperation> _mkdirLogger;
    private readonly OperationQueue _operationQueue;
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly IDialogService _dialogs;
    private readonly WindowsFileSystem _fs;
    private readonly HotkeyManager _hotkeyManager;
    /// <summary>Needed so an imported settings bundle can repaint without a restart (6.20).</summary>
    private readonly ThemeManager _themeManager;
    private readonly TerminalService _terminalService;
    /// <summary>The shells the command line runs a typed command in.</summary>
    private readonly ShellService _shells;
    private readonly ExternalToolService _externalTools;
    private readonly TCV.Git.GitStatusProvider _gitStatusProvider;
    /// <summary>Worktrees, writes, clone and the worktree setup hook — one dependency instead of
    /// four (see <see cref="MainViewModel"/>'s git half in <c>MainViewModel.Git.cs</c>).</summary>
    private readonly GitWorkflowServices _git;
    /// <summary>Recorded sessions, MCP server, write gate, LLM client and semantic index — one
    /// dependency instead of six (agent half lives in <c>MainViewModel.Agents.cs</c>).</summary>
    private readonly AgentServices _agents;
    private readonly IArchivePlugin _archivePlugin;
    private readonly OperationLog _operationLog;
    private readonly SearchPresetStore _searchPresets = new();
    /// <summary>Instant name index behind the whole-disk search (6.21).</summary>
    private readonly Navigation.NameIndexService _nameIndex;
    /// <summary>Named, replayable operations (6.22).</summary>
    private readonly SavedOperationStore _savedOperations;
    /// <summary>Files gathered across directories, awaiting one copy or move (6.24).</summary>
    private readonly DropStack _dropStack;
    /// <summary>Round-trips a file that is not on a reachable disk through a temp copy (6.23).</summary>
    private readonly RemoteEditService _remoteEdit;
    /// <summary>Sessions kept alive so a save minutes from now still uploads.</summary>
    private readonly List<RemoteEditSession> _remoteEditSessions = new();
    private readonly StartupOptions _startup;

    public ObservableCollection<Bookmark> Bookmarks { get; } = new();

    /// <summary>The drop-stack count in the menu bar, hidden while the stack is empty (6.51).</summary>
    public DropStackBadge DropStackBadge { get; }

    // ── Command facades (6.26) ───────────────────────────────────────────────
    //
    // The shell keeps the panels, the selection, the mount guard and the operation plumbing;
    // everything else lives in one of these four, each with its own dependencies.

    /// <summary>Workspaces, their sidebar, notes, terminal and the <c>*.tcvproj</c> layer.</summary>
    public WorkspaceCommands Workspaces { get; }

    /// <summary>Saved connections, mounting a panel on them, and the transfer queue.</summary>
    public MountCommands Mounts { get; }

    /// <summary>
    /// True while a file operation or a background transfer is running or waiting — closing the
    /// window then would cut it off half way (a copy leaves a partial file behind).
    /// </summary>
    public bool HasRunningWork =>
        _operationQueue.Running.Count > 0 || _operationQueue.Pending.Count > 0 || Mounts.HasActiveTransfers;

    /// <summary>Settings, hotkeys, plugins, editors and agent CLIs, bookmarks, updates.</summary>
    public ServiceCommands Services { get; }

    /// <summary>Copy, move, delete, rename, open, the clipboard, drag &amp; drop, pack/unpack.</summary>
    public FileCommands Files { get; }

    /// <summary>The product name in the window caption (ADR-0036).</summary>
    public const string ProductName = "TCVibe";

    /// <summary>Window caption: the shell owns it, the workspace facade writes the project name into it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _windowTitle = ProductName;

    /// <param name="services">The application-wide singletons, bundled — see <see cref="ShellServices"/>
    /// for why the shell takes a bundle where the facades list what they use.</param>
    public MainViewModel(
        PanelViewModel leftPanel,
        PanelViewModel rightPanel,
        ShellServices services,
        TCV.Git.GitStatusProvider gitStatusProvider,
        GitWorkflowServices git,
        AgentServices agents,
        StartupOptions? startupOptions = null)
    {
        _logger = services.Logger;
        _copyLogger = services.CopyLogger;
        _moveLogger = services.MoveLogger;
        _deleteLogger = services.DeleteLogger;
        _mkdirLogger = services.MkdirLogger;
        _operationQueue = services.Operations;
        _settings = services.Settings;
        _serializer = services.Serializer;
        _dialogs = services.Dialogs;
        _fs = services.FileSystem;
        _hotkeyManager = services.Hotkeys;
        _themeManager = services.Themes;
        _terminalService = services.Terminal;
        _shells = services.Shells;
        _externalTools = services.ExternalTools;
        _gitStatusProvider = gitStatusProvider;
        _git = git;
        _agents = agents;
        _operationLog = services.OperationLog;
        _nameIndex = services.NameIndex;
        _savedOperations = services.SavedOperations;
        _dropStack = services.DropStack;
        DropStackBadge = new DropStackBadge(_dropStack, DropStackBadge.PostToDispatcher);
        _remoteEdit = services.RemoteEdit;
        _startup = startupOptions ?? StartupOptions.Default;
        _archivePlugin = services.Plugins.Registry.FileSystems
            .OfType<IArchivePlugin>()
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Archive plugin not found");

        LeftPanel = leftPanel;
        RightPanel = rightPanel;
        LeftPanel.IsActive = true;
        SplitVertical = _settings.SplitVertical;

        // Restore last paths from settings
        foreach (var b in _settings.Bookmarks)
            Bookmarks.Add(b);

        LeftPanel.LoadInitialPath(_settings.LeftPanel.LastPath);
        LeftPanel.FileList.ApplySettings(_settings.LeftPanel);

        RightPanel.LoadInitialPath(_settings.RightPanel.LastPath);
        RightPanel.FileList.ApplySettings(_settings.RightPanel);
        WireFolderTree();

        // The facades are built last: each of them reads the panels, so they need a shell that is
        // already standing (6.26).
        Workspaces = new WorkspaceCommands(this, _settings, _dialogs, _logger, _terminalService)
        {
            ShowWorkspaceSidebar = _settings.ShowWorkspaceSidebar,
        };
        Mounts = new MountCommands(this, _dialogs, _logger, services.Connections, services.Transfers);
        Services = new ServiceCommands(
            this, _settings, _serializer, _dialogs, _hotkeyManager, services.Plugins,
            services.PluginLoader, _terminalService, _shells, _externalTools, services.Updates, _operationLog);
        Files = new FileCommands(
            this, _settings, _dialogs, _fs, _operationQueue, services.Clipboard, _archivePlugin,
            _agents, _logger, services.CopyLogger, services.MoveLogger, services.DeleteLogger,
            services.MkdirLogger, services.FileEditor);

        // The agent layer's singletons need the live panels, and this window is transient — so the
        // window registers itself rather than being injected into them.
        _agents.Attach(this);
        InitializeChrome();

        // Launched as "open this workspace in a new window" — the restored snapshot replaces the
        // last-session paths loaded just above.
        if (_startup.WorkspaceName is { } requested)
        {
            if (Workspaces.SwitchToWorkspaceByName(requested))
            {
                return;
            }

            // The name resolved to nothing: this is an ordinary session on the last-used paths.
            // The shared options are downgraded too, so the MCP server and the settings guards
            // (which read them) agree with this window that it is a primary one.
            _startup.DowngradeToPrimary();
            _logger.LogWarning("Workspace '{Workspace}' from --workspace was not found — starting a normal session", requested);
        }

        // The panels came back from the ordinary session; re-attach them to the workspace they
        // belonged to so the colour, title, notes and terminal command follow.
        Workspaces.RestoreActiveFromSettings();
    }

    /// <summary>
    /// True when this window was opened for one workspace by another window. Such a window is a
    /// guest: it does not own the session, so it must not write <c>settings.json</c>. Workspaces
    /// themselves it may edit — those writes are per-entry (see <see cref="WorkspaceStore.Touch"/>).
    /// </summary>
    public bool IsSecondaryWindow => _startup.IsSecondaryWindow;

    [ObservableProperty]
    private PanelViewModel _leftPanel;

    [ObservableProperty]
    private PanelViewModel _rightPanel;

    [ObservableProperty]
    private bool _splitVertical;

    public PanelViewModel ActivePanel => LeftPanel.IsActive ? LeftPanel : RightPanel;
    internal PanelViewModel OppositePanel => LeftPanel.IsActive ? RightPanel : LeftPanel;

    [RelayCommand]
    private void ActivateLeftPanel() => ActivatePanel(left: true);

    [RelayCommand]
    private void ActivateRightPanel() => ActivatePanel(left: false);

    /// <summary>Moves focus between the panels. Also what restoring a workspace snapshot uses.</summary>
    internal void ActivatePanel(bool left)
    {
        LeftPanel.IsActive = left;
        RightPanel.IsActive = !left;
        OnPropertyChanged(nameof(ActivePanel));
    }

    /// <summary>File system the active panel reads from — the local disk, or whatever it mounted.</summary>
    internal IVirtualFileSystem SourceVfs => ActivePanel.FileList.ActiveVfs;

    /// <summary>File system the opposite (destination) panel writes to.</summary>
    internal IVirtualFileSystem TargetVfs => OppositePanel.FileList.ActiveVfs;

    /// <summary>
    /// Declines a command that hands the selection to Windows by path — shell verbs, packing, the
    /// local tools, an editor or an agent — unless the active panel's entries are disk files (INV-07).
    /// Search hits are (TRAP-28); an archive's or a server's entries would resolve against an
    /// unrelated local path. A command that also needs a folder takes
    /// <see cref="FileListViewModel.WorkingFolder"/> (where it works) or
    /// <see cref="FileListViewModel.DiskFolder"/> (where it puts something).
    /// </summary>
    internal async Task<bool> RejectIfNotOnDiskAsync(string title)
    {
        var fileList = ActivePanel.FileList;
        if (fileList.EntriesOnDisk)
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(title, LocalizationManager.Get(fileList.IsInsideArchive
            ? "Fc_DiskOnlyArchive"
            : "Fc_DiskOnlyMounted"));
        return true;
    }

    /// <summary>
    /// Declines a command that needs the panel to be a plain disk folder, search results included:
    /// it reads <c>CurrentPath</c> as a Windows path, and in a mounted panel that is "/" or "/pub".
    /// A command that only needs real paths for its entries uses <see cref="RejectIfNotOnDiskAsync"/>.
    /// </summary>
    internal async Task<bool> RejectIfMountedAsync(string title)
    {
        var fileList = ActivePanel.FileList;
        if (!fileList.IsMounted)
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(title, LocalizationManager.Get(
            fileList.IsInsideArchive ? "Fc_DiskOnlyArchive"
            : fileList.IsSearchResults ? "Fc_DiskOnlySearchResults"
            : "Fc_DiskOnlyMounted"));
        return true;
    }

    /// <summary>
    /// The disk folder the active panel works in (<see cref="FileListViewModel.WorkingFolder"/>: git,
    /// a terminal, an agent), or null once the refusal is shown. Search results have one — the folder
    /// the list was opened over (ADR-0026); an archive or a server has none.
    /// </summary>
    internal async Task<string?> WorkingFolderOrRejectAsync(string title)
    {
        if (ActivePanel.FileList.WorkingFolder is { } folder)
        {
            return folder;
        }

        await RejectIfMountedAsync(title);
        return null;
    }

    /// <summary>
    /// The disk folder the active panel stands in, for a command that puts something there
    /// (<see cref="FileListViewModel.DiskFolder"/>: paste, a new file, a clone), or null once the
    /// refusal is shown. A hit's folder opened from the search results is one; the list itself is not.
    /// </summary>
    internal async Task<string?> DiskFolderOrRejectAsync(string title)
    {
        if (ActivePanel.FileList.DiskFolder is { } folder)
        {
            return folder;
        }

        await RejectIfMountedAsync(title);
        return null;
    }

    /// <summary>
    /// Holds both panels' mounted file systems alive for as long as an operation runs, so
    /// disconnecting or navigating out mid-transfer cannot dispose the connection it streams
    /// through. Disposing the handle lets a pending Unmount finish its cleanup.
    /// </summary>
    internal IDisposable LeasePanelFileSystems() => new CompositeDisposable(
        ActivePanel.FileList.LeaseActiveVfs(),
        OppositePanel.FileList.LeaseActiveVfs());

    private sealed class CompositeDisposable : IDisposable
    {
        private readonly IDisposable[] _parts;
        public CompositeDisposable(params IDisposable[] parts) => _parts = parts;
        public void Dispose()
        {
            foreach (var part in _parts)
                part.Dispose();
        }
    }

    /// <summary>Closes the progress dialog and surfaces the failure, if the operation did not complete.</summary>
    internal async Task FinishOperationAsync(
        FileOperationBase op, ProgressDialogViewModel progress, string errorTitle)
    {
        if (op.State == OperationState.Completed)
        {
            progress.Complete();
            await ReportWarningsAsync(op);
            return;
        }

        progress.Dismiss();
        // Not after Abort in the retry / skip / abort prompt: the user has just read that error.
        if (op.HasUnreportedFailure)
            await _dialogs.ShowErrorAsync(errorTitle, GetFileErrorMessage(op.FailureException!));
    }

    /// <summary>
    /// An operation that completed but left things out (junctions, items skipped after an error)
    /// says so; a silent partial copy is how people lose files they thought they had.
    /// </summary>
    internal async Task ReportWarningsAsync(FileOperationBase op)
    {
        var warnings = op.Warnings;
        if (warnings.Count == 0) return;

        const int Shown = 8;
        var lines = string.Join(Environment.NewLine, warnings.Take(Shown));
        if (warnings.Count > Shown)
            lines += Environment.NewLine + string.Format(LocalizationManager.Get("Op_MoreWarnings"), warnings.Count - Shown);

        await _dialogs.ShowInfoAsync(
            LocalizationManager.Get("Op_PartialTitle"),
            LocalizationManager.Format("Op_PartialBody", warnings.Count)
                + Environment.NewLine + Environment.NewLine + lines);
    }

    /// <summary>
    /// Handler for a per-item failure inside an operation: puts retry / skip / abort to the user.
    /// Every user-driven operation gets one; headless callers (agents) get none and fail fast.
    /// </summary>
    internal OperationErrorHandler BuildErrorHandler() => (error, _) =>
        _dialogs.ShowOperationErrorAsync(error);

    internal static string GetFileErrorMessage(Exception ex) => ex switch
    {
        DestinationInsideSourceException inside
            => string.Format(LocalizationManager.Get("Op_IntoItself"), inside.ItemName),
        IOException ioe when (ioe.HResult & 0xFFFF) == 32
            => LocalizationManager.Get("Op_FileInUse"),
        UnauthorizedAccessException
            => LocalizationManager.Get("Op_AccessDenied"),
        TCV.Archives.ArchivePasswordRequiredException
            => LocalizationManager.Get("Fc_ArchivePasswordNeeded"),
        TCV.Archives.ArchiveEntryEncryptedException
            => LocalizationManager.Get("Fc_ArchiveEntryEncrypted"),
        _ => ex.Message
    };

    [RelayCommand]
    private void SwitchPanel()
    {
        if (LeftPanel.IsActive) ActivateRightPanel();
        else ActivateLeftPanel();
    }

    /// <summary>
    /// Shutdown: persists the session and tears down what must not outlive the window.
    /// Anything that only wants the settings written mid-session calls <see cref="PersistState"/>
    /// instead — closing a remote-edit session there would delete the temporary copy out from
    /// under an editor the user still has open (6.23).
    /// </summary>
    public void SaveState()
    {
        // A leftover watcher would keep uploading into a connection this window is about to close.
        CloseRemoteEditSessions();

        // The workspace this window is on keeps the tabs and folders it ended up with. A per-entry
        // write, so it is safe from a guest window too (TRAP-22).
        Workspaces.SaveActiveSnapshot();

        // Recorded agent sessions are closed too. Once this process is gone it can no longer observe
        // anything, so a session left open would go on claiming every future change under its root as
        // that agent's work — a week-old session would attribute today's edits to it (6.12).
        //
        // Not from a guest window (TRAP-22): agent-sessions.json is shared with the window that owns
        // the session, and closing every session from here would end the primary window's still
        // running agents, whose edits would then come back as External. Guarded here rather than in
        // PersistState because this is a teardown, not a write of our own state.
        if (IsSecondaryWindow)
        {
            _logger.LogInformation("Secondary workspace window — agent sessions left open");
        }
        else
        {
            var closed = _agents.Sessions.CloseAll();
            if (closed > 0) _logger.LogInformation("Closed {Count} open agent session(s)", closed);
        }

        PersistState();
    }

    /// <summary>
    /// Writes the session — the active workspace's layout and the settings — and leaves everything
    /// running. An update saves this way before it hands over: the updater may end this process
    /// without waiting for the window to close (TRAP-72), and a hand-over that fails must leave the
    /// window as it was.
    /// </summary>
    public void SaveSession()
    {
        Workspaces.SaveActiveSnapshot();
        PersistState();
    }

    /// <summary>Raised when the window should close the normal way (it still asks about running work and saves).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Closes the window as its close button would: the view listens to <see cref="ExitRequested"/>.</summary>
    public void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Writes the live session state into the settings — nothing is disposed.</summary>
    private void PersistState()
    {
        // A workspace window opened from another window does not own the session (6.6): saving
        // here would push its workspace's layout into settings.json as "where you last were".
        if (IsSecondaryWindow)
        {
            _logger.LogInformation("Secondary workspace window — session state not saved");
            return;
        }

        // Each panel persists its own LastPath — the disk path underneath any mount, since
        // "/pub" or an archive path would not resolve on the next launch.
        LeftPanel.SaveState();
        RightPanel.SaveState();
        _settings.SplitVertical = SplitVertical;
        _settings.ActiveWorkspace = Workspaces.ActiveWorkspace?.Name ?? string.Empty;
        _settings.Bookmarks.Clear();
        foreach (var b in Bookmarks)
            _settings.Bookmarks.Add(b);
        _serializer.Save(_settings);
        _logger.LogInformation("State saved");
    }

    internal IReadOnlyList<TCV.Core.Interfaces.IFileEntry> GetSelectedOrCurrent()
    {
        var selected = ActivePanel.FileList.SelectedFiles;
        if (selected.Count > 0) return selected.Where(vm => !vm.IsParentEntry).Select(vm => vm.Entry).ToList();
        if (ActivePanel.FileList.SelectedItem is { IsParentEntry: false } item) return new[] { item.Entry };
        return Array.Empty<TCV.Core.Interfaces.IFileEntry>();
    }

    internal IConflictStrategy BuildConflictStrategy(ConflictMode mode)
    {
        return mode switch
        {
            ConflictMode.Overwrite => new OverwriteStrategy(),
            ConflictMode.Skip => new SkipStrategy(),
            ConflictMode.RenameAuto => new RenameAutoStrategy(),
            _ => BuildAskStrategy(),
        };
    }

    internal IConflictStrategy BuildAskStrategy()
    {
        ConflictResolution? applyAll = null;

        // One question at a time: a parallel copy meets conflicts on several threads, and without the
        // gate the dialogs stacked up and "apply to all" never reached the ones already waiting.
        var gate = new SemaphoreSlim(1, 1);

        return new AskUserStrategy(async (conflict, ct) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (applyAll.HasValue)
                    return applyAll.Value;

                var (resolution, applyToAll) = await _dialogs.ShowConflictDialogAsync(
                    conflict.Source, conflict.Existing);

                if (applyToAll)
                    applyAll = resolution;

                return resolution;
            }
            finally
            {
                gate.Release();
            }
        });
    }
}

/// <param name="Extended">Shift+right-click: the shell menu shows its extended verbs, as in Explorer.</param>
public sealed record ContextMenuRequest(
    FileEntryViewModel? Target,
    IReadOnlyList<FileEntryViewModel> SelectedItems,
    string FolderPath,
    System.Windows.Point ScreenPoint,
    bool Extended = false);
