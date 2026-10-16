using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Settings;

namespace TCV.App.ViewModels;

/// <summary>
/// The window's chrome around the panels (ADR-0030): the title bar, the tool strip, the agent strip,
/// the command line and the status line. They show what the rest of the shell already knows; the
/// project-level facts are gathered by <see cref="Status"/>.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Branch, changes, the latest agent run, origin marks, operations, AI and MCP.</summary>
    public ProjectStatusViewModel Status { get; private set; } = null!;

    /// <summary>«portable · v4.0.0» at the end of the status line: state lives next to the exe, always (INV-03).</summary>
    public string VersionText { get; } =
        "portable · v" + (typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0");

    /// <summary>What the window is about, in the status line: the workspace, else the repository, else the folder.</summary>
    public string ProjectName =>
        Workspaces?.ActiveWorkspace?.Name is { Length: > 0 } workspace ? workspace
        : Status?.RepositoryName is { Length: > 0 } repository ? repository
        : FolderName(ActivePanel.FileList.WorkingFolder) ?? string.Empty;

    /// <summary>
    /// The active workspace with its icon, in the middle of the title bar; empty with no workspace.
    /// Written with the window caption (<see cref="WorkspaceCommands.ApplyAppearance"/>), so a switch,
    /// a rename or a new icon shows at once.
    /// </summary>
    [ObservableProperty]
    private string _workspaceTitle = string.Empty;

    /// <summary>The prompt of the command line: the folder a command typed there runs in.</summary>
    public string CommandPrompt => (ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath) + ">";

    /// <summary>What is typed into the command line.</summary>
    [ObservableProperty]
    private string _commandText = string.Empty;

    /// <summary>The shell a command typed into the command line runs in, named in the box left of the prompt.</summary>
    [ObservableProperty]
    private string _shellName = string.Empty;

    /// <summary>
    /// True while the light palette is on; the tool strip shows the moon then, the sun otherwise, and the
    /// title bar takes the logo without the rim the dark bar needs.
    /// </summary>
    public bool IsLightTheme => _themeManager.CurrentTheme == "Light";

    /// <summary>
    /// The window's tone: the active workspace's colour, else the accent. The strip under the menu, the
    /// selected workspace and the status dot wear it (the design's --workspace).
    /// </summary>
    public System.Windows.Media.Brush ToneBrush =>
        (System.Windows.Media.Brush?)Workspaces?.WorkspaceAccentBrush
        ?? System.Windows.Application.Current?.TryFindResource("AccentBrush") as System.Windows.Media.Brush
        ?? System.Windows.Media.Brushes.Gray;

    // ── The side bar's disk ──────────────────────────────────────────────────

    /// <summary>«Local Disk C:» under the side bar; empty when the active panel is on no listed drive.</summary>
    [ObservableProperty] private string _diskCaption = string.Empty;

    /// <summary>How full the drive is, 0…1, for the bar.</summary>
    [ObservableProperty] private double _diskUsed;

    /// <summary>«182 GB free of 512 GB».</summary>
    [ObservableProperty] private string _diskFreeText = string.Empty;

    [ObservableProperty] private bool _hasDisk;

    private void UpdateDisk()
    {
        var drive = ActivePanel.DriveBar.SelectedDrive;
        if (drive is null || drive.TotalSize <= 0)
        {
            HasDisk = false;
            return;
        }

        // The status bar measures free space after every listing; the drive entry is from the last drive read.
        var free = ActivePanel.StatusBar.FreeSpace > 0 ? ActivePanel.StatusBar.FreeSpace : drive.FreeSpace;
        DiskCaption = FolderTreeNodes.DriveCaption(drive.Label, drive.DriveType, drive.Name, parentheses: false);
        DiskUsed = Math.Clamp(1 - free / (double)drive.TotalSize, 0, 1);
        DiskFreeText = LocalizationManager.Format(
            "Shell_DiskFree",
            Converters.FileSizeConverter.FormatCompact(free),
            Converters.FileSizeConverter.FormatCompact(drive.TotalSize));
        HasDisk = true;
    }

    private void InitializeChrome()
    {
        Status = new ProjectStatusViewModel(
            _git.Write,
            _operationLog,
            _agents.Sessions,
            _agents.Mcp,
            _agents.Llm,
            () => _operationQueue.Running.Count + _operationQueue.Pending.Count + (Mounts.HasActiveTransfers ? 1 : 0));

        Status.OriginsChanged += (_, _) => PushOrigins();
        Status.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProjectStatusViewModel.RepositoryName))
            {
                OnPropertyChanged(nameof(ProjectName));
            }
        };

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            var source = panel;
            source.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PanelViewModel.CurrentPath) && ReferenceEquals(source, ActivePanel))
                {
                    TrackActiveFolder();
                }
            };
            source.DriveBar.PropertyChanged += (_, _) =>
            {
                if (ReferenceEquals(source, ActivePanel))
                {
                    UpdateDisk();
                }
            };
            source.StatusBar.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(StatusBarViewModel.FreeSpace) && ReferenceEquals(source, ActivePanel))
                {
                    UpdateDisk();
                }
            };
        }

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActivePanel))
            {
                TrackActiveFolder();
            }
        };

        Workspaces.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceCommands.ActiveWorkspaceName))
            {
                OnPropertyChanged(nameof(ProjectName));
            }

            if (e.PropertyName == nameof(WorkspaceCommands.WorkspaceAccentBrush))
            {
                OnPropertyChanged(nameof(ToneBrush));
            }
        };

        LocalizationManager.LanguageChanged += (_, _) => UpdateDisk();

        // The name picked last shows at once; the shells installed here are looked up in the pool, so
        // the picker has them when it opens (INV-06).
        ShellName = _shells.SelectedName;
        _ = RefreshShellAsync();

        // The side bar is remembered now; it is built the first time it shows.
        if (_settings.ShowFolderTree)
        {
            ShowFolderTree = true;
            _ = ShowFolderTreeAtStartupAsync();
        }

        TrackActiveFolder();
    }

    private async Task ShowFolderTreeAtStartupAsync()
    {
        await RefreshFolderTreeRootsAsync();
        await FollowActivePanelInFolderTreeAsync();
    }

    partial void OnShowFolderTreeChanged(bool value) => _settings.ShowFolderTree = value;

    /// <summary>The active panel moved: the status follows its folder, the prompt and the title too.</summary>
    private void TrackActiveFolder()
    {
        Status.Track(ActivePanel.FileList.WorkingFolder);
        OnPropertyChanged(nameof(CommandPrompt));
        OnPropertyChanged(nameof(ProjectName));
        UpdateDisk();
    }

    /// <summary>Something may have changed behind our back (the window was activated): look again.</summary>
    public void RefreshProjectStatus() => Status.Request();

    private void PushOrigins()
    {
        LeftPanel.FileList.Origins = Status.Origins;
        RightPanel.FileList.Origins = Status.Origins;
    }

    private static string? FolderName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    // ── Commands of the tool strip ────────────────────────────────────────────

    /// <summary>
    /// Ctrl+U — the two panels trade folders, as in Total Commander. A mounted panel (archive, server,
    /// search results) cannot be carried over: a mount belongs to its panel (TRAP-38), so the swap is
    /// declined with a notice rather than half done.
    /// </summary>
    [RelayCommand]
    private void SwapPanels()
    {
        if (LeftPanel.FileList.IsMounted || RightPanel.FileList.IsMounted)
        {
            ShowNotice(LocalizationManager.Get("Shell_SwapMounted"), TimeSpan.FromSeconds(4));
            return;
        }

        var left = LeftPanel.CurrentPath;
        var right = RightPanel.CurrentPath;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LeftPanel.CurrentPath = right;
        RightPanel.CurrentPath = left;
    }

    /// <summary>The sun / moon button: light and dark palettes swap at once and the choice is kept («follow Windows» goes off).</summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        _settings.Theme.ThemeName = IsLightTheme ? "Dark" : "Light";
        _settings.Theme.UseSystemTheme = false;
        ApplyAppearance();
    }

    /// <summary>The shells the command line's picker offers: the configured ones, else those installed here.</summary>
    public Task<IReadOnlyList<ShellProfile>> GetShellsAsync() => _shells.AvailableAsync();

    /// <summary>A shell picked in the command line: the next command runs in it, and the choice is kept.</summary>
    [RelayCommand]
    private void SelectShell(ShellProfile? shell)
    {
        if (shell is null)
        {
            return;
        }

        _shells.Select(shell.Name);
        ShellName = shell.Name;
    }

    /// <summary>
    /// The shells may have changed (startup, saved settings): the box names the one a command runs in
    /// now — the first one when the picked shell is gone.
    /// </summary>
    public async Task RefreshShellAsync()
    {
        try
        {
            ShellName = (await _shells.CurrentAsync()).Name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up the command line's shells");
        }
    }

    /// <summary>
    /// Enter in the command line. <c>cd &lt;path&gt;</c> moves the active panel (relative to its folder),
    /// whatever the shell; anything else runs in the chosen shell in a terminal in the panel's folder. It
    /// is the user's own text in that shell's syntax, so it is passed on as typed, never rebuilt — only
    /// quoted for the shell's command line (<see cref="Shells.ShellCommandLine"/>, TRAP-41).
    /// </summary>
    [RelayCommand]
    private async Task RunCommandLine()
    {
        var text = CommandText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var fileList = ActivePanel.FileList;
        if (CommandLine.TryParseChangeDirectory(text, out var target))
        {
            CommandText = string.Empty;
            var path = CommandLine.ResolveAgainst(fileList.WorkingFolder, target);
            if (!await fileList.TryNavigateToTypedPathAsync(path))
            {
                ShowNotice(LocalizationManager.Format("Shell_CdNotFound", path), TimeSpan.FromSeconds(4));
            }

            return;
        }

        var folder = await WorkingFolderOrRejectAsync(LocalizationManager.Get("Shell_CommandLineTitle"));
        if (folder is null)
        {
            return;
        }

        var shell = await _shells.CurrentAsync();
        CommandText = string.Empty;
        if (!_shells.Run(shell, folder, text))
        {
            ShowNotice(LocalizationManager.Format("Shell_CommandFailed", shell.Name), TimeSpan.FromSeconds(4));
        }
    }
}
