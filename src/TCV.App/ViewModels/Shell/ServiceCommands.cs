using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Commands;
using TCV.App.FileSystem;
using TCV.App.Localization;
using TCV.App.PluginHost;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Interfaces;
using TCV.Tools;

namespace TCV.App.ViewModels;

/// <summary>
/// The commands that serve the application rather than the files in it: settings and hotkeys,
/// the plugin manager, checksums, user tools, the operation log, editors and agent CLIs, the
/// terminal, bookmarks, the update check and feedback. One of the four command facades
/// <see cref="MainViewModel"/> was split into by task 6.26.
/// </summary>
public sealed partial class ServiceCommands : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly IDialogService _dialogs;
    private readonly HotkeyManager _hotkeyManager;
    private readonly PluginHost.PluginHost _pluginHost;
    private readonly PluginLoader _pluginLoader;
    private readonly TerminalService _terminalService;
    private readonly ShellService _shells;
    private readonly ExternalToolService _externalTools;
    private readonly UpdateService _updateService;
    private readonly OperationLog _operationLog;
    private readonly ChecksumEngine _checksumEngine = new();
    private UserToolsPlugin? _userToolsPlugin;

    public ServiceCommands(
        MainViewModel shell,
        AppSettings settings,
        SettingsSerializer serializer,
        IDialogService dialogs,
        HotkeyManager hotkeyManager,
        PluginHost.PluginHost pluginHost,
        PluginLoader pluginLoader,
        TerminalService terminalService,
        ShellService shells,
        ExternalToolService externalTools,
        UpdateService updateService,
        OperationLog operationLog)
    {
        _shell = shell;
        _settings = settings;
        _serializer = serializer;
        _dialogs = dialogs;
        _hotkeyManager = hotkeyManager;
        _pluginHost = pluginHost;
        _pluginLoader = pluginLoader;
        _terminalService = terminalService;
        _shells = shells;
        _externalTools = externalTools;
        _updateService = updateService;
        _operationLog = operationLog;
        RefreshUserTools();
        // A rebind in the hotkey dialog can take a tool's key away (or give it back).
        _hotkeyManager.BindingsChanged += (_, _) => RebuildUserToolMenu();
    }

    // ── What this facade borrows from the shell ──────────────────────────────

    private PanelViewModel ActivePanel => _shell.ActivePanel;
    private System.Collections.ObjectModel.ObservableCollection<Bookmark> Bookmarks => _shell.Bookmarks;

    private IReadOnlyList<IFileEntry> GetSelectedOrCurrent() => _shell.GetSelectedOrCurrent();
    private Task<bool> RejectIfNotOnDiskAsync(string title) => _shell.RejectIfNotOnDiskAsync(title);

    /// <summary>Recording an agent launch as a session belongs to the agent layer, not here.</summary>
    private bool StartAgentSession(ExternalTool tool, string directory, string task) =>
        _shell.StartAgentSession(tool, directory, task);

    // Alt+Enter — Settings
    [RelayCommand]
    private Task OpenSettings() => ShowSettingsAsync(initialTab: null);

    /// <summary>
    /// «Manage gadgets…» (6.50): the settings on their Gadgets tab, which is where gadgets are
    /// added, edited and deleted — the Ctrl+F2 picker only runs them.
    /// </summary>
    [RelayCommand]
    private Task OpenGadgetsManager() => ShowSettingsAsync(SettingsDialogViewModel.GadgetsTab);

    /// <summary>
    /// «Command-line shells…»: the settings on their Shells tab, where shells are added, edited and
    /// deleted — the picker in the command line only chooses one.
    /// </summary>
    [RelayCommand]
    private Task OpenShellsManager() => ShowSettingsAsync(SettingsDialogViewModel.ShellsTab);

    private async Task ShowSettingsAsync(string? initialTab)
    {
        var mcpWasEnabled = _settings.Agent.McpEnabled;
        var mcpOldPort = _settings.Agent.McpPort;
        var vm = new SettingsDialogViewModel(_settings, _serializer, _externalTools, _shells)
        {
            EditorGesture = GestureText.Format(_hotkeyManager.GetBinding("OpenInEditor")),
            AgentGesture = GestureText.Format(_hotkeyManager.GetBinding("RunAgentHere")),
            InitialTab = initialTab,
        };
        await _dialogs.ShowSettingsAsync(vm);

        // Saved values only reach the running window if someone pushes them there: theme, colour
        // rules and the file-list options were read once, at startup.
        if (vm.SavedSuccessfully)
        {
            _shell.ApplyChangedSettings(vm.HiddenFilesChanged, vm.FoldersFirstChanged);
            await _shell.ApplyMcpSettingsAsync(mcpWasEnabled, mcpOldPort);
            await _shell.RefreshShellAsync();
        }
    }

    // Checksum tool — MD5/SHA1/SHA256
    [RelayCommand]
    private async Task OpenChecksum()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        // The engine hashes files by opening them from disk by path; search hits are such (TRAP-28),
        // and the checksum file is offered in the folder the panel works in.
        if (await _shell.WorkingFolderOrRejectAsync(LocalizationManager.Get("Checksum_Unavailable")) is not { } folder) return;

        var vm = new ChecksumDialogViewModel(selected, folder, _checksumEngine);
        await _dialogs.ShowChecksumAsync(vm);
    }

    // User-defined tools manager
    [RelayCommand]
    private async Task OpenUserToolsManager()
    {
        var plugin = UserTools;
        if (plugin is null)
        {
            await _dialogs.ShowInfoAsync(LocalizationManager.Get("AppTitle"), LocalizationManager.Get("UserTools_NotLoaded"));
            return;
        }

        // A gesture that already runs a built-in command is not given to a tool (TRAP-21): the dialog
        // asks who owns it so it can warn, instead of leaving the user with a shortcut that never fires.
        var vm = new UserToolsDialogViewModel(plugin, (first, second) => second is { } next
            ? _hotkeyManager.GetChordCommandId(first.Key, first.Modifiers, next.Key, next.Modifiers)
            : _hotkeyManager.GetCommandId(first.Key, first.Modifiers));
        await _dialogs.ShowUserToolsAsync(vm);

        // The dialog writes straight into the plugin, so the menu and the gestures have to be
        // rebuilt from it — otherwise a tool added here stays unreachable until the next launch.
        RefreshUserTools();
    }

    // ── Running user tools (6.29) ────────────────────────────────────────────
    //
    // Until 6.29 the dialog above only edited a list: nothing implemented IToolContext and nothing
    // called UserToolsPlugin.ExecuteTool. A tool now reaches the user through three routes — the
    // Tools menu, its own gesture, and the command palette — all of them going through the one
    // command id below.

    /// <summary>The plugin holding the user's tool list, or null when it failed to load.</summary>
    private UserToolsPlugin? UserTools =>
        _userToolsPlugin ??= _pluginHost.Tools.OfType<UserToolsPlugin>().FirstOrDefault();

    /// <summary>Prefix of the command id a user tool is dispatched under: <c>UserTool:&lt;name&gt;</c>.</summary>
    public const string UserToolCommandPrefix = "UserTool:";

    /// <summary>The tools as the menu and the palette show them, in the order the dialog left them.</summary>
    public System.Collections.ObjectModel.ObservableCollection<UserTool> UserToolItems { get; } = new();

    /// <summary>
    /// The "Run a tool" submenu rows. The shortcut shown is the one that really resolves to the tool
    /// (<see cref="HotkeyManager.GetBinding"/>), not the text typed in the tools dialog: a tool whose
    /// key a built-in command already owns must not advertise it.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<UserToolMenuEntry> UserToolMenuEntries { get; } = new();

    private void RebuildUserToolMenu()
    {
        UserToolMenuEntries.Clear();
        foreach (var tool in UserToolItems)
            UserToolMenuEntries.Add(new UserToolMenuEntry(
                tool,
                Controls.MenuHeaderText.Literal(tool.Name),
                GestureText.Format(_hotkeyManager.GetBinding(CommandId(tool)))));
    }

    /// <summary>A copy of the user's tools for the settings bundle (6.20).</summary>
    public List<UserTool> UserToolsForExport() => UserTools?.Tools.ToList() ?? new();

    /// <summary>
    /// Replaces the user's tools with an imported list and saves them where the tools dialog does:
    /// in the plugin's store, which is the list the menu, the palette and the keys are built from.
    /// </summary>
    public void ImportUserTools(IReadOnlyList<UserTool> tools)
    {
        if (UserTools is not { } plugin)
        {
            return;
        }

        plugin.Tools.Clear();
        plugin.Tools.AddRange(tools);
        plugin.SaveTools();
        RefreshUserTools();
    }

    /// <summary>Rebuilds the menu list and re-registers the tools' own gestures.</summary>
    public void RefreshUserTools()
    {
        UserToolItems.Clear();
        foreach (var tool in UserTools?.Tools ?? [])
            if (!string.IsNullOrWhiteSpace(tool.Name)) UserToolItems.Add(tool);

        _hotkeyManager.SetUserToolBindings(
            UserToolItems
                .Where(t => !string.IsNullOrWhiteSpace(t.Hotkey))
                .Select(t => (CommandId(t), t.Hotkey!)));
        RebuildUserToolMenu();
    }

    /// <summary>Palette rows for the user's tools, appended to the static catalogue.</summary>
    public IReadOnlyList<CommandPaletteItem> UserToolPaletteItems(string lang)
    {
        var category = LocalizationManager.Get("Palette_UserTools");
        return UserToolItems
            .Select(t => new CommandPaletteItem(
                CommandId(t), t.Name, category,
                GestureText.Format(_hotkeyManager.GetBinding(CommandId(t)))))
            .ToList();
    }

    /// <summary>Resolves a <c>UserTool:…</c> command id and runs it; false when no such tool exists.</summary>
    public bool TryRunUserTool(string commandId)
    {
        if (!commandId.StartsWith(UserToolCommandPrefix, StringComparison.Ordinal)) return false;

        var name = commandId[UserToolCommandPrefix.Length..];
        var tool = UserToolItems.FirstOrDefault(
            t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null) return false;

        RunUserToolCommand.Execute(tool);
        return true;
    }

    /// <summary>Runs one user tool against the current panels.</summary>
    [RelayCommand]
    private void RunUserTool(UserTool? tool)
    {
        if (tool is null || UserTools is not { } plugin) return;
        if (string.IsNullOrWhiteSpace(tool.Command)) return;

        // The macros (%P, %S, %T) expand to paths a process has to be able to open, and an archive
        // or a server has none — the same rule the gadgets and the shell verbs live by (TRAP-02).
        // Search hits are disk files (TRAP-28).
        if (!ActivePanel.FileList.EntriesOnDisk)
        {
            _ = _dialogs.ShowErrorAsync(tool.Name, LocalizationManager.Get("UserTool_MountedRejected"));
            return;
        }

        var context = PanelToolContext.Capture(ActivePanel, _shell.OppositePanel, GetSelectedOrCurrent());

        try
        {
            plugin.ExecuteTool(tool, context);
        }
        catch (TCV.Core.Domain.UnsafeMacroValueException ex)
        {
            _ = _dialogs.ShowErrorAsync(
                tool.Name,
                string.Format(LocalizationManager.Get("Tool_UnsafeArgs"), tool.Name, ex.Value));
        }
        catch (PanelFolderUnavailableException ex)
        {
            // %T names the other panel's folder, and an archive, a server or the search-results
            // list is none: "/pub" used to reach the tool as "\pub", a folder on the current drive.
            _ = _dialogs.ShowErrorAsync(
                tool.Name,
                string.Format(LocalizationManager.Get(
                    ex.Macro == 'T' ? "UserTool_TargetNotOnDisk" : "UserTool_MountedRejected"), tool.Name));
        }
        catch (Exception ex)
        {
            _ = _dialogs.ShowErrorAsync(
                tool.Name,
                string.Format(LocalizationManager.Get("UserTool_LaunchFailed"), tool.Name, tool.Command)
                    + Environment.NewLine + ex.Message);
        }
    }

    private static string CommandId(UserTool tool) => UserToolCommandPrefix + tool.Name;

    /// <summary>Shows the file-operation log (copy/move/delete/new-folder history).</summary>
    [RelayCommand]
    private void OpenOperationLog() => _dialogs.ShowOperationLog(_operationLog, _shell.UndoLoggedRenameAsync);

    // Plugin Manager dialog (3.3)
    [RelayCommand]
    private Task OpenPluginManager() =>
        _dialogs.ShowPluginManagerAsync(new PluginManagerDialogViewModel(_pluginHost, _pluginLoader));

    // Hotkey Settings dialog (3.5)
    [RelayCommand]
    private Task OpenHotkeySettings() =>
        _dialogs.ShowHotkeySettingsAsync(new HotkeySettingsDialogViewModel(_hotkeyManager, _settings, _serializer));

    // ── Help > feedback (ADR-0036): bugs go to the issue form, the rest to discussions ──

    /// <summary>
    /// Opens the bug form with this copy's version and Windows filled in. The user sees the form in
    /// the browser and sends it there: the program itself sends nothing.
    /// </summary>
    [RelayCommand]
    private Task ReportBug()
    {
        var version = typeof(ServiceCommands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? _updateService.CurrentVersion.ToString(3);
        var build = ProjectLinks.DescribeBuild(version, RuntimeInformation.RuntimeIdentifier, PortableLayout.IsVelopack);
        var system = ProjectLinks.DescribeSystem(
            Environment.OSVersion.Version, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
        return OpenWebPageAsync(ProjectLinks.BugReportUrl(build, system));
    }

    [RelayCommand]
    private Task OpenDiscussions() => OpenWebPageAsync(ProjectLinks.DiscussionsUrl);

    /// <summary>Shows the logs in Explorer with the newest one selected, ready to attach to a bug report.</summary>
    [RelayCommand]
    private async Task OpenLogFolder()
    {
        var folder = TCV.Core.Domain.AppPaths.Data("logs");
        try
        {
            Directory.CreateDirectory(folder);
            var newest = new DirectoryInfo(folder).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            var arguments = newest is null ? $"\"{folder}\"" : $"/select,\"{newest.FullName}\"";
            using var explorer = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Feedback_Title"),
                LocalizationManager.Format("Feedback_LogFolderFailed", folder, ex.Message));
        }
    }

    /// <summary>Opens a page in the default browser; when Windows cannot, gives the address to open by hand.</summary>
    private async Task OpenWebPageAsync(string url)
    {
        if (!_updateService.OpenInBrowser(url))
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Feedback_Title"),
                LocalizationManager.Format("Feedback_OpenFailed", url));
        }
    }

    // Help > Check for Updates
    [RelayCommand]
    private async Task CheckForUpdate()
    {
        var title = LocalizationManager.Get("Update_Title");
        // The request may take a while (10 s timeout); say that something is happening. Cleared
        // however the check ends, and only if no newer notice has replaced it meanwhile.
        var checking = LocalizationManager.Get("Update_Checking");
        _shell.ShowNotice(checking);
        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckAsync();
        }
        finally
        {
            _shell.ClearNotice(checking);
        }

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.Failed:
                // No answer is not "up to date": say the check did not work.
                await _dialogs.ShowErrorAsync(title,
                    string.Format(LocalizationManager.Get("Update_CheckFailed"), result.Error));
                return;

            case UpdateCheckOutcome.UpToDate:
                await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Update_UpToDate"));
                return;

            case UpdateCheckOutcome.NoReleases:
                await _dialogs.ShowInfoAsync(title,
                    string.Format(LocalizationManager.Get("Update_NoReleases"), _updateService.ReleasesPageUrl));
                return;
        }

        var update = result.Update!;
        var notes = string.Join(Environment.NewLine, update.ReleaseNotes.Split('\n').Take(5).Select(l => l.TrimEnd('\r')));

        if (!update.CanInstall)
        {
            var download = await _dialogs.ConfirmAsync(title,
                UpdatePrompt("Update_AvailableMessage", update.LatestVersion, notes));

            if (download && !_updateService.OpenInBrowser(update.DownloadUrl))
            {
                await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Update_OpenFailed"));
            }

            return;
        }

        // A guest window owns nothing (TRAP-22, TRAP-36), and installing closes every window of the program.
        if (_shell.IsSecondaryWindow)
        {
            await _dialogs.ShowInfoAsync(title,
                string.Format(LocalizationManager.Get("Update_InstallFromMain"), update.LatestVersion));
            return;
        }

        if (await _dialogs.ConfirmAsync(title,
                UpdatePrompt("Update_InstallMessage", update.LatestVersion, notes)))
        {
            await InstallUpdateAsync(title);
        }
    }

    /// <summary>The prompt without the empty paragraph a release without notes would leave in it.</summary>
    private static string UpdatePrompt(string key, Version version, string notes) =>
        System.Text.RegularExpressions.Regex.Replace(
            string.Format(LocalizationManager.Get(key), version, notes.Trim()), @"(\r?\n){3,}", "\n\n");

    /// <summary>
    /// Downloads the update and hands over to Velopack's updater, which swaps <c>current\</c> once this
    /// process is gone and starts the new version (ADR-0031). Everything the updater would cut off is
    /// settled first: no operation may be running, the workspace windows close the normal way, and the
    /// session is on disk before the hand-over (TRAP-72).
    /// </summary>
    private async Task InstallUpdateAsync(string title)
    {
        if (_shell.HasRunningWork)
        {
            await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Update_Busy"));
            return;
        }

        var lastNotice = string.Empty;
        var lastPercent = -1;
        try
        {
            await _updateService.DownloadAsync(percent =>
            {
                // Velopack reports every chunk; only a new percentage is worth a dispatcher round-trip.
                if (percent == lastPercent)
                {
                    return;
                }

                lastPercent = percent;
                lastNotice = string.Format(LocalizationManager.Get("Update_Downloading"), percent);
                _shell.ShowNotice(lastNotice);
            });
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("Update_DownloadFailed"), ex.Message));
            return;
        }
        finally
        {
            _shell.ClearNotice(lastNotice);
        }

        // The download takes a while: work started meanwhile is cut off by the restart just the same.
        if (_shell.HasRunningWork)
        {
            await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Update_Busy"));
            return;
        }

        if (await Task.Run(() => SiblingWindows.CloseAll(TimeSpan.FromSeconds(20))) > 0)
        {
            await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Update_WindowsStillOpen"));
            return;
        }

        // The window stayed usable while the workspace windows closed. Checked again with nothing
        // awaited between here and the exit: a close cancelled over running work would leave the
        // updater waiting, and it kills this process when its wait runs out (TRAP-72).
        if (_shell.HasRunningWork)
        {
            await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Update_Busy"));
            return;
        }

        _shell.SaveSession();
        try
        {
            _updateService.ApplyAfterExit();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("Update_ApplyFailed"), ex.Message));
            return;
        }

        _shell.RequestExit();
    }

    /// <summary>
    /// Says, once the window is up, that a downloaded update is not installed. Either the install
    /// stopped before the hand-over (work was running, a workspace window stayed open), or the updater
    /// found files in <c>current\</c> held by another program, gave up and started the old version again.
    /// </summary>
    public async Task ReportUpdateNotInstalledAsync()
    {
        if (_shell.IsSecondaryWindow || !_updateService.CanInstall)
        {
            return;
        }

        if (await Task.Run(_updateService.NotInstalledVersion) is { } version)
        {
            await _dialogs.ShowInfoAsync(LocalizationManager.Get("Update_Title"),
                string.Format(LocalizationManager.Get("Update_NotInstalled"), version));
        }
    }

    // Ctrl+Alt+T — Open terminal in current directory
    [RelayCommand]
    private async Task OpenTerminal()
    {
        // A mounted panel's path is "/" or "/home/user": Windows reads the first as the current
        // drive's root and the terminal opened there, the second it could not find and said nothing.
        // Search results work in the folder they were listed over (ADR-0026).
        var title = LocalizationManager.Get("Terminal_Open");
        if (await _shell.WorkingFolderOrRejectAsync(title) is not { } directory)
        {
            return;
        }

        if (!await Task.Run(() => System.IO.Directory.Exists(directory)))
        {
            await _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("Terminal_NoDirectory"), directory));
            return;
        }

        _terminalService.OpenIn(directory);
    }

    // ──────────────────── Editors and agent CLIs (Ctrl+E / Ctrl+Shift+A) ────────────────────

    // Ctrl+E — open the selection (or the folder) in the default editor
    [RelayCommand]
    private Task OpenInEditor() => LaunchExternalToolAsync(isEditor: true, pick: false);

    // Ctrl+Shift+E — choose the editor; the choice becomes the default
    [RelayCommand]
    private Task OpenInEditorPicker() => LaunchExternalToolAsync(isEditor: true, pick: true);

    // Ctrl+Shift+A — start an agent CLI in the panel's directory
    [RelayCommand]
    private Task RunAgentHere() => LaunchExternalToolAsync(isEditor: false, pick: false);

    // Ctrl+Alt+A — choose which agent to start
    [RelayCommand]
    private Task RunAgentPicker() => LaunchExternalToolAsync(isEditor: false, pick: true);

    /// <summary>
    /// Shared body of the four commands above: an editor or an agent, taken as the remembered default
    /// or chosen from the fuzzy picker. Both need a real directory on disk, so an archive or a server
    /// is rejected up front (INV-07) rather than handing an external process a path like <c>/pub</c>.
    /// Search hits are disk files, and their list works in the folder it was opened over (TRAP-28).
    /// </summary>
    private async Task LaunchExternalToolAsync(bool isEditor, bool pick)
    {
        var titleKey = isEditor ? "Editor_Title" : "Agent_Title";
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get(titleKey)))
        {
            return;
        }

        var tools = isEditor ? _externalTools.AvailableEditors() : _externalTools.AvailableAgents();
        if (tools.Count == 0)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get(titleKey),
                LocalizationManager.Get(isEditor ? "Editor_NoneFound" : "Agent_NoneFound"));
            return;
        }

        // The plain gesture goes straight to the remembered default; the picker gesture asks and
        // then makes that choice the new default, so the fast path stays fast.
        var tool = pick ? await PickExternalToolAsync(tools, titleKey) : DefaultOf(isEditor);
        if (tool is null) return;   // picker cancelled

        if (pick)
        {
            if (isEditor) _externalTools.RememberDefaultEditor(tool.Name);
            else _externalTools.RememberDefaultAgent(tool.Name);
        }

        // An agent works on the directory; an editor also receives whatever is selected.
        var directory = ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath;
        var selected = isEditor
            ? GetSelectedOrCurrent()
                .Select(e => WindowsFileSystem.ToWindowsPath(e.Path))
                .ToList()
            : new List<string>();

        // An agent launch is recorded as a session, an editor launch is not: the session exists so
        // the «Agent changes» dashboard can attribute edits later (6.12), and an editor's edits are
        // the user's own.
        // Resolving an editor's command probes PATH, which can hang on a dead share: off the UI
        // thread (INV-06). The agent path records a session first and stays where it is.
        string? refused = null;
        bool started;
        if (isEditor)
        {
            (started, refused) = await Task.Run(() =>
            {
                var ok = _externalTools.Launch(tool, directory, selected, out var reason);
                return (ok, reason);
            });
        }
        else
        {
            started = StartAgentSession(tool, directory, string.Empty);
        }

        if (!started)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get(titleKey),
                refused is null
                    ? string.Format(LocalizationManager.Get("Tool_LaunchFailed"), tool.Name, tool.Command)
                    : string.Format(LocalizationManager.Get("Tool_UnsafeArgs"), tool.Name, refused));
        }
    }

    private ExternalTool? DefaultOf(bool isEditor) =>
        isEditor ? _externalTools.DefaultEditor() : _externalTools.DefaultAgent();

    /// <summary>Reuses the command-palette control as a fuzzy tool picker, as the workspace switcher does.</summary>
    private async Task<ExternalTool?> PickExternalToolAsync(
        IReadOnlyList<ExternalTool> tools, string titleKey)
    {
        var items = tools
            .Select(t => new CommandPaletteItem(t.Name, t.Name, t.Command, string.Empty))
            .ToList();

        var vm = new CommandPaletteViewModel(
            items, LocalizationManager.CurrentLanguage,
            LocalizationManager.Get(titleKey), LocalizationManager.Get("Tool_PickPlaceholder"));
        if (await _dialogs.PickFromPaletteAsync(vm) is not { } chosen)
        {
            return null;
        }
        return tools.FirstOrDefault(t => t.Name == chosen.Id);
    }

    // Ctrl+D — Add current folder to bookmarks
    [RelayCommand]
    private async Task AddBookmark()
    {
        // A mounted panel shows "/pub" or an archive-internal path: as a bookmark it would point
        // nowhere. The disk directory underneath is what survives a restart (TRAP-03).
        var fileList = ActivePanel.FileList;
        var path = fileList.IsMounted ? fileList.RootLocalPath : ActivePanel.CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var defaultName = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(defaultName))
        {
            defaultName = path;
        }

        // A --workspace guest window does not own settings.json (TRAP-22), so the bookmark lives only
        // as long as the window. Said in the prompt itself — after the fact it used to just vanish.
        var name = await _dialogs.ShowInputAsync(
            LocalizationManager.Get("Bookmarks_AddTitle"),
            LocalizationManager.Get(_shell.IsSecondaryWindow ? "Bookmarks_AddPromptGuest" : "Bookmarks_AddPrompt"),
            defaultName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var bookmark = new Bookmark { Name = name.Trim(), Path = path };
        _settings.Bookmarks.Add(bookmark);
        Bookmarks.Add(bookmark);

        // A guest window keeps the bookmark until it closes (the prompt said so above).
        if (!_shell.IsSecondaryWindow)
        {
            _serializer.Save(_settings);
        }
    }

    // Navigate to a bookmark path
    [RelayCommand]
    private async Task NavigateToBookmark(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // A folder that is gone (deleted, drive unplugged) is reported instead of leaving the panel
        // on an empty listing. Asked off the UI thread: the folder may sit on a sleeping share (INV-06).
        if (!await Task.Run(() => System.IO.Directory.Exists(path)))
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Bookmarks_Title"),
                string.Format(LocalizationManager.Get("Bookmarks_PathMissing"), path));
            return;
        }

        // Leaves an open archive or connection first: the bookmark is a disk path.
        _shell.NavigateActiveToDisk(path);
    }

    // Open bookmarks manager dialog
    [RelayCommand]
    private async Task OpenBookmarksManager()
    {
        var fileList = ActivePanel.FileList;
        var current = fileList.IsMounted ? fileList.RootLocalPath : ActivePanel.CurrentPath;

        var vm = new BookmarksManagerDialogViewModel(
            _settings, _dialogs, current,
            _shell.IsSecondaryWindow ? null : () => _serializer.Save(_settings));   // TRAP-22
        vm.BookmarksChanged += (_, _) =>
        {
            Bookmarks.Clear();
            foreach (var b in _settings.Bookmarks)
                Bookmarks.Add(b);
        };
        await _dialogs.ShowBookmarksManagerAsync(vm);
    }
}

/// <summary>One row of the "Run a tool" submenu: the tool, its menu-safe header and its live shortcut.</summary>
public sealed record UserToolMenuEntry(UserTool Tool, string Header, string Gesture);
