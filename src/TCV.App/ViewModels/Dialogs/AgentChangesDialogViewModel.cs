using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.Ai.Agents;
using TCV.App.Agents;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>What the dashboard was closed for, in the same shape as the worktree dashboard.</summary>
public enum AgentChangesAction
{
    None,

    /// <summary>Show <see cref="AgentChangesDialogViewModel.ChosenPath"/> in the active panel.</summary>
    ShowInPanel,
}

/// <summary>One recorded agent session as the sessions tab shows it: text, local times, no booleans.</summary>
public sealed record AgentSessionRow(
    string Id, string Tool, string Task, string Root, string StartedText, bool IsOpen, string OpenText)
{
    public static AgentSessionRow From(AgentSession session) => new(
        session.Id,
        session.Tool,
        // A multi-line task would make one row as tall as a paragraph.
        session.Task.Replace("\r\n", " ↵ ").Replace('\n', ' '),
        session.Root,
        session.StartedUtc.ToLocalTime().ToString("g"),
        session.IsOpen,
        LocalizationManager.Get(session.IsOpen ? "Agent_Yes" : "Agent_No"));
}

/// <summary>
/// The «Agent changes» dashboard (6.12) — the flagship of the agent-aware layer and the one part of
/// it that needs no LLM, no MCP and no network.
/// <para>
/// The bet behind it (ADR-0009): the 2026 bottleneck is not producing changes but
/// <em>checking</em> them. A two-panel manager already has the three things a review surface needs —
/// a git working-tree view, an operation log, and undo — and this window is those three joined by one
/// question: for each changed file, who wrote it? The answer degrades honestly rather than pretending
/// (see <see cref="ChangeAttribution"/>), because a review tool that overstates what it knows is worse
/// than one that shows the gap.
/// </para>
/// </summary>
public partial class AgentChangesDialogViewModel : ObservableObject
{
    private readonly GitWriteService _git;
    private readonly string _anyPathInRepository;
    private readonly AgentSessionStore _sessions;
    private readonly OperationLog _operationLog;
    private readonly AgentWriteGate _gate;
    private readonly McpServerHost _mcp;

    /// <summary>Confirms a revert, naming what is about to be lost (INV-11). Supplied by the dialog.</summary>
    private readonly Func<IReadOnlyList<AgentChangeRow>, bool> _confirmRevert;

    /// <summary>Deletes untracked files through the operation queue (INV-10). Returns false on refusal.</summary>
    private readonly Func<IReadOnlyList<AgentChangeRow>, Task<bool>> _discardUntracked;

    /// <summary>Opens the git diff window for one file.</summary>
    private readonly Action<string> _showDiff;

    /// <summary>Asks for a directory to approve for agent access.</summary>
    private readonly Func<string?> _pickFolder;

    /// <summary>
    /// Asks whether a wide directory (a drive root, the user profile) really is meant. Null accepts
    /// everything — for a caller with no window to ask from.
    /// </summary>
    private readonly Func<string, bool>? _confirmBroadRoot;

    /// <summary>Where revert backups go: beside the executable (INV-03). A parameter so tests can point elsewhere.</summary>
    private readonly string _backupDirectory;

    /// <summary>
    /// What <c>panel://</c> reaches right now (the archives and connections open in the panels that the
    /// access rule lets through). Null when the caller has no panels to describe.
    /// </summary>
    private readonly Func<IReadOnlyList<string>>? _reachableMounts;

    /// <summary>Everything the last refresh found, before the «only attributed» filter.</summary>
    private IReadOnlyList<AgentChangeRow> _allRows = [];

    public AgentChangesDialogViewModel(
        GitWriteService git,
        string anyPathInRepository,
        AgentSessionStore sessions,
        OperationLog operationLog,
        AgentWriteGate gate,
        McpServerHost mcp,
        Func<IReadOnlyList<AgentChangeRow>, bool> confirmRevert,
        Func<IReadOnlyList<AgentChangeRow>, Task<bool>> discardUntracked,
        Action<string> showDiff,
        Func<string?> pickFolder,
        Func<string, bool>? confirmBroadRoot = null,
        string? backupDirectory = null,
        Func<IReadOnlyList<string>>? reachableMounts = null)
    {
        _git = git;
        _anyPathInRepository = anyPathInRepository;
        _sessions = sessions;
        _operationLog = operationLog;
        _gate = gate;
        _mcp = mcp;
        _confirmRevert = confirmRevert;
        _discardUntracked = discardUntracked;
        _showDiff = showDiff;
        _pickFolder = pickFolder;
        _confirmBroadRoot = confirmBroadRoot;
        _backupDirectory = backupDirectory ?? global::TCV.Core.Domain.AppPaths.DataDirectory;
        _reachableMounts = reachableMounts;

        WindowTitle = LocalizationManager.Get("Agent_ChangesTitle");

        Pending = gate.Pending;
        RecentWrites = gate.Recent;
    }

    public string WindowTitle { get; }

    // ─────────────────────────────── Changes ───────────────────────────────

    /// <summary>Working-tree changes with their provenance.</summary>
    public ObservableCollection<AgentChangeRow> Changes { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevert))]
    [NotifyPropertyChangedFor(nameof(CanDiff))]
    private AgentChangeRow? _selected;

    [ObservableProperty]
    private string _branch = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRevert));

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Hide changes nobody can be shown to have made. Off by default: the point of the window is that
    /// you see everything and the column tells you how sure we are — filtering by default would let a
    /// change slip past because our attribution was too weak, which is the opposite of review.
    /// </summary>
    [ObservableProperty]
    private bool _onlyAttributed;

    // Filtering what is already loaded: toggling the box used to run a full git status again.
    partial void OnOnlyAttributedChanged(bool value) => ApplyFilter();

    public bool CanRevert => Selected is not null && !IsBusy;

    public bool CanDiff => Selected is { RevertMeansDelete: false };

    /// <summary>Count line under the list — how much of this is an agent's work.</summary>
    public string Summary
    {
        get
        {
            if (Changes.Count == 0) return string.Empty;

            var byAgent = Changes.Count(c => c.Origin is ChangeOrigin.Agent or ChangeOrigin.Supervised);
            return LocalizationManager.Format("Agent_Summary", Changes.Count, byAgent);
        }
    }

    // ─────────────────────────────── Journal & sessions ───────────────────────────────

    /// <summary>Operation-log lines, newest first — the "what did the manager actually do" half.</summary>
    /// <remarks>The operation log's own row type, so kind, outcome and the recycle bin read in the user's language.</remarks>
    public ObservableCollection<OperationLogRow> Journal { get; } = new();

    /// <summary>Recorded agent runs; an open one is still claiming changes.</summary>
    public ObservableCollection<AgentSessionRow> Sessions { get; } = new();

    [ObservableProperty]
    private AgentSessionRow? _selectedSession;

    /// <summary>Write proposals waiting for an answer right now (6.13b).</summary>
    public ObservableCollection<AgentWriteProposal> Pending { get; }

    /// <summary>Proposals already decided this session.</summary>
    public ObservableCollection<AgentWriteRecord> RecentWrites { get; }

    // ─────────────────────────────── Server state ───────────────────────────────

    public bool ServerRunning => _mcp.IsRunning;

    /// <summary>«Running» / «Stopped» in words — a bare True/False says nothing about a server.</summary>
    public string ServerStateText => LocalizationManager.Get(ServerRunning ? "Agent_ServerOn" : "Agent_ServerOff");

    /// <summary>
    /// False in a guest workspace window, where the server and its approvals belong to the window that
    /// owns the session (TRAP-22). The commands below still check it — the buttons are disabled, but a
    /// disabled button is a display, not a guarantee.
    /// </summary>
    public bool CanConfigureServer => _mcp.CanConfigure;

    public string ServerUrl => _mcp.Endpoint?.Url ?? string.Empty;

    /// <summary>Ready-to-paste registration line, so connecting a client is copy-and-paste.</summary>
    public string ServerCommandLine => _mcp.Endpoint?.ClientCommandLine() ?? string.Empty;

    public string ServerError => _mcp.LastErrorText;

    /// <summary>The directories an agent can reach — the honest disclosure required by § 3.6.4.</summary>
    public ObservableCollection<string> ApprovedRoots { get; } = new();

    [ObservableProperty]
    private string _selectedRoot = string.Empty;

    /// <summary>
    /// The other half of the disclosure: what <c>panel://</c> reads among the panels' mounts — an
    /// archive inside an approved directory, a server connection when that is allowed in settings
    /// (ADR-0023). The list above names directories, not the mounts that fall under them.
    /// </summary>
    public string ReachableMountsText
    {
        get
        {
            var mounts = _reachableMounts?.Invoke() ?? [];
            return mounts.Count == 0
                ? LocalizationManager.Get("Agent_ExposedMountsNone")
                : string.Format(LocalizationManager.Get("Agent_ExposedMounts"), string.Join("; ", mounts));
        }
    }

    [ObservableProperty]
    private bool _writesEnabled;

    partial void OnWritesEnabledChanged(bool value)
    {
        if (_applyingServerState) return;

        if (!RefuseIfSecondaryWindow())
        {
            _mcp.SetWritesEnabled(value);
            return;
        }

        // Put the switch back: the setting was not changed, so the UI must not claim it was.
        RefreshServerState();
    }

    /// <summary>Guards the writes we make to bound properties while reading state back from settings.</summary>
    private bool _applyingServerState;

    // ─────────────────────────────── Commands ───────────────────────────────

    public AgentChangesAction Action { get; private set; } = AgentChangesAction.None;
    public string ChosenPath { get; private set; } = string.Empty;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Re-reads everything. Kept off the constructor so the window is up before the status walk —
    /// on a large repository that is not instant, same as the worktree dashboard.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        OnPropertyChanged(nameof(CanRevert));
        StatusMessage = LocalizationManager.Get("Agent_Loading");
        try
        {
            var path = _anyPathInRepository;

            // Status, the operation log, the sessions file and one File.GetLastWriteTime per changed
            // file — all disk work, all off the dispatcher (INV-06).
            var (status, operations, sessions, rows) = await Task.Run(async () =>
            {
                var gitStatus = await _git.GetStatusAsync(path);
                var log = _operationLog.LoadAll();
                var recorded = _sessions.Load();
                return (gitStatus, log, recorded, AgentChangeSet.Build(gitStatus, log, recorded, DateTime.UtcNow));
            });

            Branch = status.Branch;
            _allRows = rows;

            Journal.Clear();
            foreach (var entry in operations.Take(200)) Journal.Add(new OperationLogRow(entry));

            Sessions.Clear();
            foreach (var session in sessions.Take(50)) Sessions.Add(AgentSessionRow.From(session));

            RefreshServerState();
            ApplyFilter();

            if (!status.IsRepository) StatusMessage = LocalizationManager.Get("Git_NoRepo");
            else if (rows.Count == 0) StatusMessage = LocalizationManager.Get("Git_Clean");
            else StatusMessage = string.Empty;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanRevert));
        }
    }

    /// <summary>Rebuilds <see cref="Changes"/> from the last refresh, honouring «only attributed».</summary>
    private void ApplyFilter()
    {
        var previous = Selected?.RelativePath;

        Changes.Clear();
        foreach (var row in _allRows)
        {
            if (OnlyAttributed && row.Origin == ChangeOrigin.External) continue;
            Changes.Add(row);
        }

        Selected = Changes.FirstOrDefault(c => c.RelativePath == previous) ?? Changes.FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// One-click revert. Which of the two things it does is decided by the row's status, and the
    /// confirmation says which: a tracked file goes back to HEAD through git, while an untracked one
    /// has nothing to go back to and is deleted through the operation queue instead.
    /// <para>
    /// Either way the file is copied aside first. The dashboard's attribution is a guess, so the file
    /// being reverted may hold the user's own uncommitted work, and <c>git restore</c> cannot bring it
    /// back. If the copy cannot be made, nothing is reverted.
    /// </para>
    /// </summary>
    [RelayCommand]
    private async Task RevertSelectedAsync()
    {
        if (Selected is not { } row) return;
        if (!_confirmRevert([row])) return;

        IsBusy = true;
        string? note = null;
        try
        {
            var backup = await Task.Run(() => RevertBackup.Save(
                _backupDirectory, [(row.RelativePath, row.FullPath)], DateTime.Now));

            if (backup is null && File.Exists(row.FullPath))
            {
                StatusMessage = LocalizationManager.Get("Agent_BackupFailed");
                return;
            }

            if (row.RevertMeansDelete)
            {
                // Unstaged before the delete, not after: unstaging loses nothing, so a delete that then
                // fails leaves an untracked file rather than a deleted-but-still-staged one.
                if (row.RevertUnstagesFirst)
                {
                    var unstaged = await _git.UnstageAsync(_anyPathInRepository, [row.RelativePath]);
                    if (!unstaged.Success)
                    {
                        StatusMessage = GitResultText.Describe(unstaged);
                        return;
                    }
                }

                if (!await _discardUntracked([row])) return;
            }
            else
            {
                var result = await _git.RestoreAsync(_anyPathInRepository, row.RestorePaths);
                if (!result.Success)
                {
                    StatusMessage = GitResultText.Describe(result);
                    return;
                }
            }

            if (backup is not null)
                note = string.Format(LocalizationManager.Get("Agent_RevertedBackup"), row.RelativePath, backup);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        if (note is not null) StatusMessage = note;
    }

    /// <summary>Stages the selected change — the review loop ends in a commit, not in a list.</summary>
    [RelayCommand]
    private async Task StageSelectedAsync()
    {
        if (Selected is not { } row) return;

        IsBusy = true;
        try
        {
            var result = await _git.StageAsync(_anyPathInRepository, [row.RelativePath]);
            if (!result.Success) StatusMessage = GitResultText.Describe(result);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private void ShowDiff()
    {
        if (Selected is { RevertMeansDelete: false } row) _showDiff(row.FullPath);
    }

    /// <summary>Hands the file to the panel, so the ordinary commands (F3, F4, F8) can act on it.</summary>
    [RelayCommand]
    private void ShowInPanel()
    {
        if (Selected is not { } row) return;

        Action = AgentChangesAction.ShowInPanel;
        ChosenPath = row.FullPath;
        Close(true);
    }

    /// <summary>
    /// Marks a session finished, so it stops claiming edits. An agent that is no longer running but
    /// whose session is still open would keep being blamed for every change under its root.
    /// </summary>
    [RelayCommand]
    private async Task CloseSessionAsync()
    {
        if (SelectedSession is not { IsOpen: true } session) return;

        _sessions.Close(session.Id);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleServerAsync()
    {
        if (RefuseIfSecondaryWindow()) return;

        IsBusy = true;
        try
        {
            await _mcp.SetEnabledAsync(!_mcp.IsRunning);
        }
        finally
        {
            IsBusy = false;
        }

        RefreshServerState();
        if (!ServerRunning && ServerError.Length > 0) StatusMessage = ServerError;
    }

    [RelayCommand]
    private void ApproveFolder()
    {
        if (RefuseIfSecondaryWindow()) return;

        if (_pickFolder() is not { Length: > 0 } folder) return;

        // A drive root or the whole user profile makes «directories an agent may reach» meaningless
        // (SSH keys, browser profiles, every other project), so it takes a second, explicit yes.
        if (_confirmBroadRoot is not null
            && TCV.Ai.Mcp.ApprovedRootAdvisor.IsBroad(
                folder,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows))
            && !_confirmBroadRoot(folder))
        {
            return;
        }

        if (_mcp.Approve(folder)) RefreshServerState();
    }

    [RelayCommand]
    private void RevokeRoot()
    {
        if (RefuseIfSecondaryWindow()) return;

        if (SelectedRoot.Length > 0 && _mcp.Revoke(SelectedRoot))
            RefreshServerState();
    }

    /// <summary>
    /// True when this window may not touch the server or the approved roots, having said so in the
    /// status line. Every server-side command starts here (TRAP-22).
    /// </summary>
    private bool RefuseIfSecondaryWindow()
    {
        if (CanConfigureServer) return false;

        StatusMessage = LocalizationManager.Get("Agent_SecondaryWindow");
        return true;
    }

    [RelayCommand]
    private void ClearWriteHistory() => _gate.ClearHistory();

    [RelayCommand]
    private void Close() => Close(false);

    private void RefreshServerState()
    {
        _applyingServerState = true;
        try
        {
            ApprovedRoots.Clear();
            foreach (var root in _mcp.CurrentRoots()) ApprovedRoots.Add(root);

            WritesEnabled = _mcp.WritesEnabled;
        }
        finally
        {
            _applyingServerState = false;
        }

        OnPropertyChanged(nameof(ServerRunning));
        OnPropertyChanged(nameof(ServerStateText));
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(ServerCommandLine));
        OnPropertyChanged(nameof(ServerError));
        OnPropertyChanged(nameof(ReachableMountsText));
    }

    private void Close(bool result)
    {
        DialogResult = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
