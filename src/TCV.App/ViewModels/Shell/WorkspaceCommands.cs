using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.ViewModels;

/// <summary>
/// Workspaces (projects): the saved list, the pinned sidebar, per-workspace notes, terminal,
/// colour and the committable <c>*.tcvproj</c> definition layer. One of the four command facades
/// <see cref="MainViewModel"/> was split into by task 6.26 — the shell keeps the panels and the
/// operation plumbing, each facade keeps one coherent group of commands.
/// </summary>
/// <remarks>
/// Everything this facade needs from the shell is declared in one place below, so the coupling is
/// a short list rather than a god object's field bag.
/// </remarks>
public sealed partial class WorkspaceCommands : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;
    private readonly TerminalService _terminalService;
    private readonly WorkspaceStore _workspaceStore = new();
    private readonly WorkspaceNotesStore _workspaceNotes = new();

    /// <summary>Name of the workspace whose snapshot the panels currently reflect, or null when the
    /// context has not been restored from / saved as any workspace this session. Persisted in
    /// <see cref="AppSettings.ActiveWorkspace"/> so a restart keeps pointing at the same project.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveWorkspace), nameof(UnpinnedActiveWorkspace))]
    private string? _activeWorkspaceName;

    public WorkspaceCommands(
        MainViewModel shell,
        AppSettings settings,
        IDialogService dialogs,
        ILogger logger,
        TerminalService terminalService)
    {
        _shell = shell;
        _settings = settings;
        _dialogs = dialogs;
        _logger = logger;
        _terminalService = terminalService;
        Refresh();
    }

    /// <summary>Saved workspaces in the rail's order (<see cref="WorkspaceOrder"/>): the pinned ones
    /// as the rail shows them top to bottom, then the rest. Alt+1..9 count down this list.</summary>
    public ObservableCollection<Workspace> Items { get; } = new();

    /// <summary>The backing <c>workspaces.json</c>. Exposed for the settings bundle (6.20), which
    /// exports and imports whole workspaces rather than going through the list shown here.</summary>
    public WorkspaceStore Store => _workspaceStore;

    // ── What this facade borrows from the shell ──────────────────────────────

    private PanelViewModel ActivePanel => _shell.ActivePanel;
    private PanelViewModel LeftPanel => _shell.LeftPanel;
    private PanelViewModel RightPanel => _shell.RightPanel;


    private static string L(string key) => LocalizationManager.Get(key);

    // ── Session layer: the saved workspaces themselves ───────────────────────

    /// <summary>
    /// Rebuilds the list (and the pinned rail) from <c>workspaces.json</c>. When the file cannot be
    /// read right now (a second window is writing it) the list on screen is left alone: blanking it
    /// would make the switcher offer "save your first workspace" over a store that is full.
    /// </summary>
    /// <remarks>
    /// The pinned ones stand where the user dragged them; the rest keep their places on screen
    /// (<see cref="WorkspaceOrder.Arrange"/>): the positional Alt+1..9 shortcuts must not move because
    /// an icon or a note was edited after a switch bumped the MRU stamp. Pinned workspaces without a
    /// place yet (a file from before 6.79, one just pinned) are numbered where they stand and the
    /// numbers written, so the rail does not reshuffle on the next launch.
    /// </remarks>
    public void Refresh()
    {
        if (_workspaceStore.TryLoad(out var loaded))
        {
            var arranged = WorkspaceOrder.Arrange(loaded, Items.ToList());
            if (WorkspaceOrder.NeedsNumbering(arranged)
                && !_workspaceStore.SetOrder(WorkspaceOrder.Number(arranged)))
            {
                _logger.LogWarning("Could not write the order of the pinned workspaces — workspaces.json is not readable/writable");
            }

            ShowArranged(arranged);
        }

        RebuildPinnedGroups();
        ReportDamagedStoreOnce();
    }

    private void ShowArranged(IReadOnlyList<Workspace> arranged)
    {
        Items.Clear();
        foreach (var ws in arranged)
        {
            Items.Add(ws);
        }
    }

    private bool _damageReported;

    /// <summary>
    /// Tells the user, once, that a damaged <c>workspaces.json</c> was replaced by its backup and
    /// where the damaged copy went. Deferred until the window is idle: the first read happens while
    /// the window is still being built, before there is anything to own a dialog.
    /// </summary>
    private void ReportDamagedStoreOnce()
    {
        if (_damageReported || _workspaceStore.CorruptCopyPath is not { } copy)
        {
            return;
        }

        _damageReported = true;
        _logger.LogWarning("workspaces.json was damaged; loaded the backup, damaged copy kept as {Copy}", copy);

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _ = _dialogs.ShowErrorAsync(
                L("Ws_StoreTitle"), string.Format(L("Ws_StoreDamaged"), Path.GetFileName(copy)))));
    }

    private void RebuildPinnedGroups()
    {
        PinnedGroups.Clear();
        foreach (var group in WorkspaceGrouping.BuildPinnedGroups(Items))
        {
            PinnedGroups.Add(group);
        }

        // Nothing pinned means nothing to show — an empty rail is just lost width.
        OnPropertyChanged(nameof(IsWorkspaceSidebarVisible));
        OnPropertyChanged(nameof(UnpinnedActiveWorkspace));
    }

    /// <summary>
    /// Writes one workspace to the store and reloads the list. Every mutation goes through here
    /// rather than through whole-file rewrites: a second window (<c>--workspace</c>) edits the same
    /// file, so a rewrite from this window's list would drop whatever that one saved meanwhile.
    /// A failed write is logged and not fatal — the change is kept in the in-memory list, which is
    /// then <em>not</em> reloaded from the disk that just refused it.
    /// </summary>
    public void Persist(Workspace ws)
    {
        if (_workspaceStore.Save(ws))
        {
            Refresh();
            return;
        }

        _logger.LogWarning("Could not persist workspace {Workspace} — workspaces.json is not readable/writable", ws.Name);
        var index = IndexOf(ws.Name);
        if (index >= 0)
        {
            Items[index] = ws;
        }
        else
        {
            Items.Add(ws);
        }

        // A pin or a group changed in memory moves the entry: the rail and Alt+N stay one order (TRAP-83).
        ShowArranged(WorkspaceOrder.Arrange(Items.ToList(), Items.ToList()));
        RebuildPinnedGroups();
    }

    /// <summary>
    /// A pinned workspace dragged on the rail to <paramref name="slot"/> of its block (6.79; 0 = before
    /// the first). Only the order is written, as a point edit (TRAP-22); when the file refuses, the
    /// move still holds for this session, as <see cref="Persist"/> keeps an edit.
    /// </summary>
    public void MoveWorkspace(string name, int slot)
    {
        if (WorkspaceOrder.Move(Items.ToList(), name, slot) is not { } order)
        {
            return;
        }

        if (_workspaceStore.SetOrder(order))
        {
            Refresh();
            return;
        }

        _logger.LogWarning("Could not reorder workspace {Workspace} — workspaces.json is not readable/writable", name);
        foreach (var ws in Items.Where(w => w.Pinned))
        {
            ws.Order = IndexIn(order, ws.Name) + 1;
        }

        ShowArranged(WorkspaceOrder.Arrange(Items.ToList(), Items.ToList()));
        RebuildPinnedGroups();
    }

    private static int IndexIn(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Pinned workspaces for the sidebar, ungrouped block first then named groups.</summary>
    public ObservableCollection<WorkspaceGroup> PinnedGroups { get; } = new();

    /// <summary>
    /// The workspace the panels are on when it is not pinned, or null. The rail shows it set apart
    /// after the pinned ones, as the taskbar shows a running program: the rail never hides where you
    /// are, and "not pinned" is something you can see — it leaves the rail when you switch away.
    /// </summary>
    public Workspace? UnpinnedActiveWorkspace => WorkspaceGrouping.UnpinnedActive(Items, ActiveWorkspaceName);

    /// <summary>User's preference for the sidebar; persisted in settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceSidebarVisible))]
    private bool _showWorkspaceSidebar;

    /// <summary>
    /// The rail shows whenever it is wanted: besides the pinned workspaces it holds the switcher, «new
    /// workspace», the terminal and help, so an empty one is still the way to make the first.
    /// </summary>
    public bool IsWorkspaceSidebarVisible => ShowWorkspaceSidebar;

    /// <summary>Shows/hides the workspace rail and remembers the choice.</summary>
    [RelayCommand]
    private void ToggleWorkspaceSidebar()
    {
        ShowWorkspaceSidebar = !ShowWorkspaceSidebar;
        _settings.ShowWorkspaceSidebar = ShowWorkspaceSidebar;
    }

    private async Task<Workspace?> ResolveAsync(Workspace? ws, string titleKey)
    {
        ws ??= ActiveWorkspace;
        if (ws is not null)
        {
            return ws;
        }

        await _dialogs.ShowErrorAsync(L(titleKey), L("WsNotes_NoWorkspace"));
        return null;
    }

    /// <summary>Captures both panels + layout under <paramref name="name"/>, keeping an existing
    /// workspace's own attributes (colour, icon, group, pin, terminal) when overwriting.
    /// <paramref name="pinIfNew"/> pins a workspace this creates.</summary>
    public void CaptureCurrent(string name, bool pinIfNew = false)
    {
        var trimmed = name.Trim();
        // Reuse the in-memory list instead of re-reading the file — it already holds every workspace.
        var existing = Items.FirstOrDefault(
            w => string.Equals(w.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        var ws = BuildSnapshot(trimmed, existing, DateTime.UtcNow);
        if (existing is null)
        {
            ws.Pinned = pinIfNew;
        }

        Persist(ws);
        ActiveWorkspaceName = ws.Name;   // the current context now *is* this workspace
        ApplyAppearance();
        if (existing is null)
        {
            RevealIfPinnedWhileHidden(ws);
        }
    }

    private Workspace BuildSnapshot(string name, Workspace? existing, DateTime lastUsedUtc)
    {
        var ws = new Workspace
        {
            Name = name,
            Left = LeftPanel.CaptureSnapshot(),
            Right = RightPanel.CaptureSnapshot(),
            ActiveLeft = LeftPanel.IsActive,
            SplitVertical = _shell.SplitVertical,
            LastUsedUtc = lastUsedUtc,
        };
        // Re-saving the layout overwrites the snapshot, not the workspace: colour, icon, group,
        // pinning and the terminal setup belong to the latter and have to survive.
        ws.CopyIdentityFrom(existing);
        return ws;
    }

    /// <summary>
    /// Writes what the panels show right now into the active workspace. A workspace is "the whole
    /// context", so leaving it — by switching, on exit — has to keep the tabs and folders the user
    /// moved to since the last explicit save; otherwise coming back restores a stale layout and
    /// silently drops the work in between. Saving under another name is not leaving (TRAP-51).
    /// </summary>
    /// <remarks>
    /// Deliberately does not reload the list: the MRU order drives the positional Alt+1..9
    /// shortcuts, and a reload after every switch would make them point somewhere else each time.
    /// The in-memory entry is updated in place instead, so switching back within this session
    /// restores the fresh layout too.
    /// </remarks>
    public void SaveActiveSnapshot()
    {
        var active = ActiveWorkspace;
        if (active is null)
        {
            return;
        }

        var snapshot = BuildSnapshot(active.Name, active, active.LastUsedUtc);
        active.CopyLayoutFrom(snapshot);

        // Only the layout goes to disk. The rest of the entry is whatever the store holds now — a
        // guest window may have recoloured or renamed it (TRAP-22), and a whole-entry save from
        // this window's copy would undo that or resurrect a deleted workspace.
        if (!_workspaceStore.UpdateLayout(active.Name, snapshot))
        {
            _logger.LogWarning("Could not save the layout of workspace {Workspace} — it is gone from workspaces.json, or the file is not readable/writable", active.Name);
        }
    }

    /// <summary>Restores a workspace: both panels, split orientation and active panel.</summary>
    public void ApplyWorkspace(Workspace? ws)
    {
        if (ws is null)
        {
            return;
        }

        // Re-applying the workspace you are already on would unmount, rebuild and reload both
        // panels, discarding every folder you navigated to since the switch — treat it as a no-op.
        if (string.Equals(ws.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Keep where the workspace we are leaving had got to.
        SaveActiveSnapshot();

        _shell.SplitVertical = ws.SplitVertical;
        LeftPanel.RestoreSnapshot(ws.Left);
        RightPanel.RestoreSnapshot(ws.Right);

        _shell.ActivatePanel(left: ws.ActiveLeft);
        ActiveWorkspaceName = ws.Name;

        // Bump MRU for the *next* launch's ordering (LoadOrdered sorts by it), but leave the
        // in-memory order untouched. Reordering on every switch would make the Alt+1..9 positional
        // shortcuts point at a different workspace after each use.
        ws.LastUsedUtc = DateTime.UtcNow;
        _workspaceStore.Touch(ws.Name, ws.LastUsedUtc);

        ApplyAppearance();
    }

    /// <summary>
    /// Re-attaches the window to the workspace it was showing at exit (the panels themselves are
    /// restored from the ordinary session). Not called in a <c>--workspace</c> window, which is
    /// opened for one workspace of its own choosing.
    /// </summary>
    public void RestoreActiveFromSettings()
    {
        var name = _settings.ActiveWorkspace;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var ws = Items.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (ws is null)
        {
            return;   // deleted since — the session simply is not tied to a workspace
        }

        ActiveWorkspaceName = ws.Name;
        ApplyAppearance();
    }

    /// <summary>Switches to a workspace by name. False when no such workspace exists.</summary>
    public bool SwitchToWorkspaceByName(string name)
    {
        // Resolve from the in-memory list so we pass the same object the collection holds (and
        // avoid a disk read) — ApplyWorkspace's MRU bump then persists that one entry.
        var ws = Items.FirstOrDefault(
            w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (ws is null)
        {
            return false;
        }

        ApplyWorkspace(ws);
        return true;
    }

    /// <summary>Switches to the N-th workspace in display order (Alt+1..9). No-op if out of range.</summary>
    public void SwitchToWorkspaceIndex(int index)
    {
        if (index >= 0 && index < Items.Count)
        {
            ApplyWorkspace(Items[index]);
        }
    }

    /// <summary>
    /// Save the current panels as a workspace. The prompt starts on the active workspace's name, so
    /// confirming it simply updates that workspace; any other existing name is replaced only after
    /// the user has said so (INV-11 — the old layout is gone once overwritten). Under another name
    /// the workspace we were on keeps the layout it last had saved.
    /// </summary>
    [RelayCommand]
    private async Task SaveWorkspace()
    {
        var suggested = ActiveWorkspaceName
                        ?? (Items.Count == 0 ? Workspace.NextNumberedName(L("Ws_DefaultName"), []) : string.Empty);
        var name = await _dialogs.ShowInputAsync(L("Ws_SaveTitle"), L("Ws_SavePrompt"), suggested);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        name = name.Trim();

        var existing = Items.FirstOrDefault(
            w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        var isActive = existing is not null
                       && string.Equals(existing.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase);

        if (existing is not null && !isActive
            && !await _dialogs.ConfirmAsync(
                L("Ws_SaveTitle"), string.Format(L("Ws_OverwriteConfirm"), existing.Name)))
        {
            return;
        }

        // TRAP-51: saving under another name is "save as" — the workspace we are on is deliberately
        // not autosaved. The panels already show the project being saved; writing them into the
        // active workspace too would give both names one layout, and switching would change nothing.
        // A new workspace is pinned, as one made with «New workspace» is: a workspace you just made
        // and then cannot find on the rail looks like it was not made at all.
        CaptureCurrent(name, pinIfNew: true);
    }

    /// <summary>
    /// Alt+Shift+W, the rail's «+» — a workspace that starts from nothing: each panel one tab in the
    /// home folder, as on a first launch. «Save as» can only copy the panels on screen, so starting a
    /// project with it meant first taking apart the layout of the workspace you were in. The window is
    /// the properties one, starting pinned, with a colour no other workspace wears yet.
    /// </summary>
    [RelayCommand]
    private Task NewWorkspace() => CreateWorkspaceAsync(folder: null);

    /// <summary>
    /// A folder dropped on the rail (6.79): a new workspace in it, through the same window as «New
    /// workspace», named after the folder, both panels in it. A folder some workspace already stands
    /// in (<see cref="WorkspaceFolderDrop.RootedAt"/>) switches to that one instead of making a second
    /// look-alike — «New workspace» still makes one on purpose. A file is refused with a notice.
    /// </summary>
    public async Task DropFolderAsync(string path)
    {
        // The folder may be on a share that is asleep (INV-06).
        if (!await Task.Run(() => Directory.Exists(path)))
        {
            _shell.ShowNotice(L("Rail_DropNotFolder"), TimeSpan.FromSeconds(4));
            return;
        }

        Refresh();
        if (WorkspaceFolderDrop.RootedAt(Items, path) is { } existing)
        {
            ApplyWorkspace(existing);
            _shell.ShowNotice(string.Format(L("Rail_DropExisting"), existing.Name), TimeSpan.FromSeconds(4));
            return;
        }

        await CreateWorkspaceAsync(path);
    }

    /// <summary>A new workspace: both panels in <paramref name="folder"/>, or in the home folder.</summary>
    private async Task CreateWorkspaceAsync(string? folder)
    {
        // A guest window (TRAP-22) may have added workspaces since this list was last read: the free
        // name and the taken-name check must see them, or Save below replaces one of them whole.
        Refresh();
        var names = Items.Select(w => w.Name).ToList();
        var draft = new Workspace
        {
            Name = folder is null
                ? Workspace.NextNumberedName(L("Ws_DefaultName"), names)
                : WorkspaceFolderDrop.NameFor(folder, names),
            Color = WorkspaceColor.SuggestFor(Items.Select(w => w.Color)),
            Pinned = true,
        };

        var result = await _dialogs.ShowWorkspacePropertiesAsync(
            draft, names, Items.Select(w => w.Group).ToList(), isNew: true);
        if (result is null)
        {
            return;   // cancelled
        }

        // The dialog checked the names known when it opened; a guest window may have taken this one
        // while it was open, and WorkspaceStore.Save replaces by name.
        if (_workspaceStore.Get(result.Name) is not null)
        {
            await _dialogs.ShowErrorAsync(L("WsProps_NewTitle"), string.Format(L("Ws_RenameTaken"), result.Name));
            return;
        }

        var start = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var ws = new Workspace
        {
            Name = result.Name,
            Icon = result.Icon,
            Color = result.Color,
            Group = result.Group,
            Pinned = result.Pinned,
            Left = WorkspacePanelState.Blank(start, LeftPanel.CaptureSnapshot()),
            Right = WorkspacePanelState.Blank(start, RightPanel.CaptureSnapshot()),
            ActiveLeft = true,
            SplitVertical = _shell.SplitVertical,
            LastUsedUtc = DateTime.UtcNow,
        };

        // Into the list first, then switch to it. Switching is leaving, so the workspace we were on
        // keeps its latest layout (unlike «save as», TRAP-51).
        Persist(ws);
        ApplyWorkspace(Items.FirstOrDefault(
            w => string.Equals(w.Name, ws.Name, StringComparison.OrdinalIgnoreCase)) ?? ws);
        RevealIfPinnedWhileHidden(ws);
    }

    /// <summary>
    /// A workspace created pinned while the sidebar is hidden turns the sidebar on and says so, as
    /// pinning does (ADR-0024): otherwise "pinned to the sidebar" changes nothing on screen.
    /// </summary>
    private void RevealIfPinnedWhileHidden(Workspace ws)
    {
        if (ws.Pinned && !ShowWorkspaceSidebar)
        {
            AnnouncePinning(ws);
        }
    }

    [RelayCommand]
    private void SwitchWorkspace(Workspace? ws) => ApplyWorkspace(ws);

    [RelayCommand]
    private async Task DeleteWorkspace(Workspace? ws)
    {
        ws = await ResolveAsync(ws, "Ws_DeleteTitle");
        if (ws is null)
        {
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            L("Ws_DeleteTitle"), string.Format(L("Ws_DeleteConfirm"), ws.Name));
        if (!confirmed)
        {
            return;
        }

        if (!_workspaceStore.Delete(ws.Name))
        {
            // The workspace stays, so its notes stay with it.
            _logger.LogWarning("Could not delete workspace {Workspace} — workspaces.json is not readable/writable", ws.Name);
            await _dialogs.ShowErrorAsync(L("Ws_DeleteTitle"), L("Ws_DeleteFailed"));
            return;
        }

        _workspaceNotes.Delete(ws.Name);   // otherwise a later workspace of the same name inherits them
        if (string.Equals(ws.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            ActiveWorkspaceName = null;
        }

        Refresh();
        ApplyAppearance();   // the deleted workspace's colour must leave the chrome with it
    }

    /// <summary>
    /// Pins/unpins a workspace: a pinned one sorts ahead of the rest (menu, switcher, Alt+1..9) and
    /// is listed in the sidebar. Pinning shows the sidebar if it was hidden — that list is where a
    /// pinned workspace lives, and pinning the first entry with the sidebar off changed nothing on
    /// screen, so the command looked broken.
    /// </summary>
    [RelayCommand]
    private async Task ToggleWorkspacePinned(Workspace? ws)
    {
        ws = await ResolveAsync(ws, "Ws_PinTitle");
        if (ws is null)
        {
            return;
        }

        ws.Pinned = !ws.Pinned;
        ws.Order = 0;   // pinned again, it goes to the end of its group on the rail, not to an old place
        Persist(ws);
        AnnouncePinning(ws);
    }

    private void AnnouncePinning(Workspace ws)
    {
        if (ws.Pinned && !ShowWorkspaceSidebar)
        {
            ShowWorkspaceSidebar = true;
            _settings.ShowWorkspaceSidebar = true;
        }

        _shell.ShowNotice(
            string.Format(L(ws.Pinned ? "Ws_PinnedNotice" : "Ws_UnpinnedNotice"), ws.Name),
            TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// Ctrl+Alt+R — name, icon, colour, group and pinning in one window. They used to be five
    /// prompts with bare text boxes (a colour typed as hex, a group nobody could guess the use of).
    /// The shortcut is the old rename's: the dialog opens with the name selected, so renaming is
    /// still "shortcut, type, Enter". Without an argument it edits the active workspace.
    /// </summary>
    [RelayCommand]
    private async Task EditWorkspaceProperties(Workspace? ws)
    {
        ws = await ResolveAsync(ws, "WsProps_Title");
        if (ws is null)
        {
            return;
        }

        var result = await _dialogs.ShowWorkspacePropertiesAsync(
            ws,
            Items.Select(w => w.Name).ToList(),
            Items.Select(w => w.Group).ToList());
        if (result is null)
        {
            return;   // cancelled
        }

        if (!string.Equals(result.Name, ws.Name, StringComparison.Ordinal))
        {
            if (!await RenameAsync(ws.Name, result.Name))
            {
                return;
            }

            // The rename reloaded the list from disk; carry on with the entry it holds now.
            ws = Items.FirstOrDefault(
                w => string.Equals(w.Name, result.Name, StringComparison.OrdinalIgnoreCase)) ?? ws;
        }

        var pinChanged = ws.Pinned != result.Pinned;
        if (pinChanged || !string.Equals(WorkspaceOrder.GroupKey(ws), result.Group.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            ws.Order = 0;   // pinned or filed under another heading: the end of that group on the rail
        }

        ws.Icon = result.Icon;
        ws.Color = result.Color;
        ws.Group = result.Group;
        ws.Pinned = result.Pinned;
        Persist(ws);
        ApplyAppearance();   // colour and icon show in the chrome and the window title

        if (pinChanged)
        {
            AnnouncePinning(ws);
        }
    }

    /// <summary>Renames a workspace, taking its notes with it. Names stay unique case-insensitively.
    /// False (after telling the user) when the store refused.</summary>
    private async Task<bool> RenameAsync(string oldName, string newName)
    {
        // The layout goes to disk under the old name first: the rename moves the stored entry, and
        // the in-memory list is reloaded from it afterwards.
        var wasActive = string.Equals(oldName, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase);
        if (wasActive)
        {
            SaveActiveSnapshot();
        }

        switch (_workspaceStore.Rename(oldName, newName))
        {
            case RenameResult.Renamed:
                break;
            case RenameResult.NameTaken:
                await _dialogs.ShowErrorAsync(L("WsProps_Title"), string.Format(L("Ws_RenameTaken"), newName));
                return false;
            default:
                await _dialogs.ShowErrorAsync(L("WsProps_Title"), L("Ws_RenameFailed"));
                return false;
        }

        if (!_workspaceNotes.Rename(oldName, newName))
        {
            _logger.LogWarning("Notes of workspace {Old} could not follow it to {New}", oldName, newName);
        }

        // Renamed in place first, so the reload below finds it where it was (Alt+N stays put).
        var shown = IndexOf(oldName);
        if (shown >= 0)
        {
            Items[shown].Name = newName;
        }

        if (wasActive)
        {
            ActiveWorkspaceName = newName;
        }

        Refresh();
        return true;
    }

    /// <summary>
    /// Ctrl+Shift+W — opens a workspace in its own window. This starts a second process rather
    /// than a second <see cref="MainWindow"/>: dialogs resolve their owner through
    /// <c>Application.Current.MainWindow</c>, so two windows in one process would send every
    /// dialog to whichever opened first. Separate processes also keep a crash in one project's
    /// window from taking the others down.
    /// </summary>
    [RelayCommand]
    private async Task OpenWorkspaceInNewWindow(Workspace? ws)
    {
        ws ??= ActiveWorkspace;
        if (ws is null)
        {
            await _dialogs.ShowErrorAsync(L("WsWindow_Title"), L("WsNotes_NoWorkspace"));
            return;
        }

        // The workspace being opened elsewhere must already be on disk, or the new process reads
        // a stale (or absent) entry and silently falls back to the last session. For the workspace
        // this window is on that means its current layout, not the one saved last week.
        if (string.Equals(ws.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            SaveActiveSnapshot();
        }
        else
        {
            _workspaceStore.Save(ws);
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "TCV.App.exe",
                Arguments = StartupOptions.GuestArguments(ws.Name, PortableLayout.CustomDataDirectory),
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open workspace {Workspace} in a new window", ws.Name);
            await _dialogs.ShowErrorAsync(L("WsWindow_Title"), string.Format(L("WsWindow_Failed"), ws.Name));
        }
    }

    // ───────────────── Workspace definition layer: committable *.tcvproj (ADR-0011) ─────────────────

    /// <summary>
    /// Writes the workspace's committable half next to the project it describes. Only the shareable
    /// parts travel (roots, identity, how to start it); tab sets and focus stay in the local
    /// session store, which is the whole point of the two-layer split.
    /// </summary>
    [RelayCommand]
    private async Task ExportWorkspaceDefinition(Workspace? ws)
    {
        ws = await ResolveAsync(ws, "WsProj_Title");
        if (ws is null)
        {
            return;
        }

        // The saved layout is what gets exported; for the workspace on screen that is the panels now.
        if (string.Equals(ws.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase))
        {
            SaveActiveSnapshot();
            ws = ActiveWorkspace ?? ws;
        }

        var path = _dialogs.PickSaveFile(
            L("WsProj_SaveTitle"),
            L("WsProj_Filter"),
            SafeFileName(ws.Name) + WorkspaceProjectFile.Extension,
            await SuggestDefinitionDirectoryAsync(ws));
        if (path is null)
        {
            return;
        }

        try
        {
            WorkspaceProjectFile.Save(path, WorkspaceProjectFile.FromWorkspace(ws, path));
            _logger.LogInformation("Workspace definition written to {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write workspace definition {Path}", path);
            await _dialogs.ShowErrorAsync(L("WsProj_Title"), string.Format(L("WsProj_SaveFailed"), ex.Message));
        }
    }

    /// <summary>A workspace name is free text; the file name it suggests must be a legal one.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "workspace" : cleaned;
    }

    /// <summary>
    /// Opens a <c>*.tcvproj</c>: resolves its relative roots against the file's own directory,
    /// registers it as a session workspace and switches to it. Cloning a repo and opening its
    /// project file is meant to be the whole setup step.
    /// </summary>
    [RelayCommand]
    private async Task OpenWorkspaceDefinition()
    {
        var path = _dialogs.PickOpenFile(
            L("WsProj_OpenTitle"),
            L("WsProj_Filter"),
            ActivePanel.FileList.IsMounted ? null : ActivePanel.CurrentPath);
        if (path is null)
        {
            return;
        }

        var definition = WorkspaceProjectFile.Load(path);
        if (definition is null)
        {
            await _dialogs.ShowErrorAsync(L("WsProj_Title"), string.Format(L("WsProj_Invalid"), path));
            return;
        }

        await ApplyDefinition(WorkspaceProjectFile.ToWorkspace(definition, path));
    }

    /// <summary>
    /// Registers a workspace built from a <c>*.tcvproj</c> and switches to it. Shared with the clone
    /// flow, which offers to open the project file a fresh checkout brought with it.
    /// </summary>
    public async Task ApplyDefinition(Workspace incoming)
    {
        // The workspace we are on keeps its latest layout before the definition takes over the panels.
        SaveActiveSnapshot();
        Refresh();

        // Opening a definition twice must update the workspace, not spawn "project (2)" — the
        // store already keys on the name, and re-applying is how you pick up a changed root. The
        // file carries only the shared half, so it is merged into the stored entry instead of
        // replacing it: tab sets, view state and pinning are session-local (ADR-0011).
        var existing = Items.FirstOrDefault(
            w => string.Equals(w.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));

        // A same-named workspace that opens none of the file's folders is another project that
        // happens to share the name: merging would silently rewrite its colour, group and terminal
        // command and push a foreign root into its tabs.
        if (existing is not null
            && !WorkspaceProjectFile.SharesRoot(incoming, existing)
            && !await _dialogs.ConfirmAsync(
                L("WsProj_Title"), string.Format(L("Ws_DefinitionNameClash"), existing.Name)))
        {
            return;
        }

        var ws = existing is null
            ? incoming
            : WorkspaceProjectFile.MergeIntoSession(incoming, existing);
        if (existing is not null)
        {
            ActiveWorkspaceName = null;   // force ApplyWorkspace to re-apply the refreshed roots
        }

        Persist(ws);

        // Apply the workspace just built from the file, not a re-read of the list: if the write did
        // not land, re-resolving by name would hand back the stale entry and the file would look
        // like it had done nothing.
        ApplyWorkspace(ws);
    }

    /// <summary>Where the save dialog should start: the workspace's own left root, since a
    /// definition normally lives in the repository it describes.</summary>
    private static async Task<string?> SuggestDefinitionDirectoryAsync(Workspace ws)
    {
        var path = ws.Left.Tabs.Count > 0
            ? ws.Left.Tabs[Math.Clamp(ws.Left.ActiveTabIndex, 0, ws.Left.Tabs.Count - 1)].Path
            : null;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        // The root may sit on a share that is asleep; asking must not freeze the window (INV-06).
        return await Task.Run(() => Directory.Exists(path)) ? path : null;
    }

    /// <summary>
    /// Pushes the active workspace's colour and name into the chrome: window title, the name in the
    /// title bar, the accent strip under the menu, and the focused panel's frame. Called on every switch — the point of
    /// a colour is telling two near-identical windows apart at a glance.
    /// </summary>
    public void ApplyAppearance()
    {
        var ws = ActiveWorkspace;

        WorkspaceAccentBrush = ws is not null && WorkspaceColor.TryParseRgb(ws.Color, out var r, out var g, out var b)
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b))
            : null;

        LeftPanel.AccentBrush = WorkspaceAccentBrush;
        RightPanel.AccentBrush = WorkspaceAccentBrush;

        _shell.WindowTitle = ws is null
            ? MainViewModel.ProductName
            : $"{ws.DisplayName} — {MainViewModel.ProductName}";
        _shell.WorkspaceTitle = ws?.DisplayName ?? string.Empty;
    }

    /// <summary>Accent brush of the active workspace, or null when it has no colour set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceAccent))]
    private System.Windows.Media.SolidColorBrush? _workspaceAccentBrush;

    /// <summary>Drives the visibility of the accent strip — no colour, no strip.</summary>
    public bool HasWorkspaceAccent => WorkspaceAccentBrush is not null;

    /// <summary>The workspace the panels currently reflect, or null when this session's context has
    /// not been restored from / saved as one. Notes and the saved terminal hang off it.</summary>
    public Workspace? ActiveWorkspace =>
        ActiveWorkspaceName is null
            ? null
            : Items.FirstOrDefault(
                w => string.Equals(w.Name, ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the workspace has notes — the menu marks those so they are worth opening.</summary>
    public bool HasNotes(Workspace ws) => _workspaceNotes.HasNotes(ws.Name);

    /// <summary>
    /// Alt+N — opens the per-workspace scratchpad: markdown notes plus the command and cwd this
    /// project is driven with. Without an argument it edits the active workspace.
    /// </summary>
    [RelayCommand]
    private async Task OpenWorkspaceNotes(Workspace? ws)
    {
        ws = await ResolveAsync(ws, "WsNotes_Title");
        if (ws is null)
        {
            return;
        }

        var result = await _dialogs.ShowWorkspaceNotesAsync(
            ws.Name,
            _workspaceNotes.Read(ws.Name),
            ws.TerminalCommand,
            ws.TerminalDirectory,
            _workspaceNotes.PathFor(ws.Name));
        if (result is null)
        {
            return;   // cancelled
        }

        _workspaceNotes.Write(ws.Name, result.Notes);

        // Only a command the user actually changed here counts as typed by them; pressing OK on a
        // command that came from a project file must not be enough to run it unseen.
        if (!string.Equals(result.TerminalCommand, ws.TerminalCommand, StringComparison.Ordinal))
        {
            ws.TrustedTerminalCommand = result.TerminalCommand;
        }

        ws.TerminalCommand = result.TerminalCommand;
        ws.TerminalDirectory = result.TerminalDirectory;
        Persist(ws);
    }

    /// <summary>
    /// Alt+T — replays the workspace's saved command in its saved directory. With no command saved
    /// this is still useful: it opens a plain shell there, which is what a project cwd is for.
    /// A command the user has not typed or approved on this machine (it arrived in a
    /// <c>*.tcvproj</c> from a clone) is shown first: a project file is somebody else's text.
    /// </summary>
    [RelayCommand]
    private async Task RunWorkspaceTerminal()
    {
        var ws = await ResolveAsync(null, "WsTerminal_Title");
        if (ws is null)
        {
            return;
        }

        var directory = ws.TerminalDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            // Falling back to the panel means handing a real path to a process — an archive or a
            // server has none (INV-07), and `/pub` would silently start the shell somewhere else.
            // Search results work in the folder they were listed over.
            if (await _shell.WorkingFolderOrRejectAsync(L("WsTerminal_Title")) is not { } folder)
            {
                return;
            }

            directory = folder;
        }

        if (!await Task.Run(() => Directory.Exists(directory)))
        {
            await _dialogs.ShowErrorAsync(
                L("WsTerminal_Title"), string.Format(L("WsTerminal_NoDirectory"), directory));
            return;
        }

        if (string.IsNullOrWhiteSpace(ws.TerminalCommand))
        {
            _terminalService.OpenIn(directory);
            return;
        }

        if (ws.NeedsTerminalConfirmation())
        {
            var approved = await _dialogs.ConfirmAsync(
                L("WsTerminal_Title"),
                string.Format(L("Ws_TerminalConfirm"), ws.TerminalCommand, directory));
            if (!approved)
            {
                return;
            }

            ws.TrustedTerminalCommand = ws.TerminalCommand;
            Persist(ws);
        }

        if (!_terminalService.OpenWithCommand(directory, ws.TerminalCommand))
        {
            await _dialogs.ShowErrorAsync(
                L("WsTerminal_Title"), string.Format(L("WsTerminal_Failed"), ws.TerminalCommand));
        }
    }
}
