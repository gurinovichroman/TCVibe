using TCV.Tools;

namespace TCV.App.Settings;

public sealed class AppSettings : System.Text.Json.Serialization.IJsonOnDeserializing
{
    public PanelSettings LeftPanel { get; set; } = new();
    public PanelSettings RightPanel { get; set; } = new();
    public OperationSettings Operations { get; set; } = new();
    public ThemeSettings Theme { get; set; } = new();

    /// <summary>Which parts of the window chrome are shown (6.33). Everything is on by default.</summary>
    public LayoutSettings Layout { get; set; } = new();
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    public List<ColorRule> ColorRules { get; set; } = new();
    /// <summary>
    /// Only the settings bundle (6.20) fills this: the live list is kept by the user-tools plugin in
    /// its own store, and an export copies it here so the file carries it.
    /// </summary>
    public List<UserTool> UserTools { get; set; } = new();
    public bool SplitVertical { get; set; } = false;
    /// <summary>Whether the workspace rail is shown. On by default, as the design has it: it also holds
    /// the switcher and «save as workspace», so it is useful before anything is pinned.</summary>
    public bool ShowWorkspaceSidebar { get; set; } = true;

    /// <summary>Whether the «Explorer» side bar (the docked folder tree, pinned folders, the disk) is shown.</summary>
    public bool ShowFolderTree { get; set; } = true;

    /// <summary>
    /// The quick search jumps instead of filtering (6.87): the cursor goes to the best match and every
    /// name stays listed, as in Total Commander, Far and Explorer. Off by default — the list narrows.
    /// </summary>
    public bool QuickSearchJumps { get; set; }

    /// <summary>
    /// Where the main window was and how large, kept while it moves and written with the session. Null
    /// until the first session ends: the window then opens centred at its design size.
    /// </summary>
    public WindowPlacementSettings? Window { get; set; }

    /// <summary>
    /// Which revision of the window's design this file was last brought up to (ADR-0030). A file
    /// written before it reads as 0 and gets <see cref="ApplyDesignDefaults"/> once. A fresh profile
    /// (no file, or defaults after an unreadable one) already has the design's defaults: it starts at
    /// the current revision, or the first session's choices would be "migrated" away on the next load
    /// — or by the settings dialog's copy, which normalizes too.
    /// </summary>
    public int DesignVersion { get; set; } = CurrentDesignVersion;

    /// <summary>A file that does not carry <see cref="DesignVersion"/> predates it: read it as 0.</summary>
    void System.Text.Json.Serialization.IJsonOnDeserializing.OnDeserializing() => DesignVersion = 0;

    /// <summary>The revision <see cref="ApplyDesignDefaults"/> brings a file to.</summary>
    public const int CurrentDesignVersion = 1;

    /// <summary>
    /// Name of the workspace the panels were showing at exit. Restored on the next launch so the
    /// colour, title and Alt+N/Alt+T keep pointing at the project the session belongs to; empty
    /// means the session was not tied to any workspace.
    /// </summary>
    public string ActiveWorkspace { get; set; } = string.Empty;
    public List<Bookmark> Bookmarks { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public ExternalToolsSettings ExternalTools { get; set; } = new();

    /// <summary>The shells the command line runs a typed command in, and which one it uses.</summary>
    public ShellSettings Shells { get; set; } = new();
    public GitSettings Git { get; set; } = new();

    /// <summary>MCP server, LLM endpoint and the agent-layer switches (Этап 6D).</summary>
    public AgentSettings Agent { get; set; } = new();

    /// <summary>
    /// User-defined command-line gadgets offered by the command palette (6.24). Empty by default:
    /// a gadget is whatever this particular user runs, so there is nothing sensible to ship.
    /// </summary>
    public List<Gadget> Gadgets { get; set; } = new();

    /// <summary>
    /// Puts back anything a hand-edited or partly written <c>settings.json</c> set to <c>null</c>.
    /// The serializer accepts a JSON null for a property that has a default, and the first use of
    /// that property would then throw far from the cause.
    /// </summary>
    public void Normalize()
    {
        LeftPanel ??= new();
        RightPanel ??= new();
        Operations ??= new();
        Theme ??= new();
        Layout ??= new();
        Hotkeys ??= new();
        ColorRules ??= new();
        UserTools ??= new();
        Bookmarks ??= new();
        Updates ??= new();
        ExternalTools ??= new();
        Shells ??= new();
        Git ??= new();
        Agent ??= new();
        Gadgets ??= new();

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            panel.Tabs ??= new();
            panel.Columns ??= new();
        }

        Theme.AccentColor ??= string.Empty;
        Theme.PanelFontFamily ??= string.Empty;
        ExternalTools.Editors ??= new();
        ExternalTools.Agents ??= new();
        ExternalTools.FileEditor ??= new();
        Shells.Profiles ??= new();
        Shells.Selected ??= string.Empty;
        Git.WorktreeCarryPatterns ??= new();
        Agent.ApprovedRoots ??= new();
        Agent.ProtectLegacyApiKey();
        ApplyDesignDefaults();
    }

    /// <summary>
    /// The design of 2026-10-12 (ADR-0030) changed defaults that older files carry written out: the
    /// columns (name, size, modified, git), the list font (11), the workspace rail and the side bar
    /// (shown). Each is moved only while it still holds the old default — a value the user chose
    /// stays. Runs once per file.
    /// </summary>
    public void ApplyDesignDefaults()
    {
        if (DesignVersion >= CurrentDesignVersion)
        {
            return;
        }

        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            if (IsOldDefaultColumns(panel.Columns))
            {
                panel.Columns = new();
            }
        }

        if (Theme.PanelFontSize == 12)
        {
            Theme.PanelFontSize = AppearanceResources.DefaultPanelFontSize;
        }
        ShowWorkspaceSidebar = true;
        ShowFolderTree = true;
        DesignVersion = CurrentDesignVersion;
    }

    /// <summary>The set the old defaults showed, in their order: name, ext, size, date, attr (widths aside).</summary>
    private static bool IsOldDefaultColumns(IReadOnlyList<ColumnLayout> columns) =>
        columns.Where(c => c.Visible).Select(c => c.Id)
            .SequenceEqual(["name", "ext", "size", "date", "attr"], StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A command line the user can run from the palette without writing a plugin (6.24).
/// </summary>
/// <remarks>
/// Distinct from <see cref="ExternalTool"/>, which answers "open this path in an editor": a gadget
/// is a whole command with its own working directory and macros, reachable by name.
/// </remarks>
public sealed class Gadget
{
    /// <summary>Shown in the palette; also the id, so it must be unique.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Executable or shim resolved through PATH, or an absolute path.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Argument template: <c>%P</c> panel directory · <c>%F</c> selected paths ·
    /// <c>%N</c> name without extension · <c>%E</c> extension.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Working directory; empty means the active panel's directory.</summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Run in a terminal window — required for anything interactive or worth reading.</summary>
    public bool InTerminal { get; set; } = true;
}

/// <summary>
/// The agent-aware layer's configuration (Этап 6D): what an agent may reach through MCP, and which
/// language-model endpoint the optional AI features use.
/// </summary>
/// <remarks>
/// Every switch here is off or empty by default, and that is a decision rather than laziness
/// (ADR-0008): a file manager that ships with a socket open and a cloud endpoint
/// configured has made a privacy choice on the user's behalf. A fresh portable copy exposes nothing
/// and contacts nothing until someone turns it on.
/// </remarks>
public sealed class AgentSettings
{
    // ── MCP server (6.13) ────────────────────────────────────────────────────

    /// <summary>Whether the MCP server runs. Off by default — it is a listening socket.</summary>
    public bool McpEnabled { get; set; } = false;

    /// <summary>Fixed port, or 0 to take a free one and publish it in <c>mcp-endpoint.json</c>.</summary>
    public int McpPort { get; set; } = 0;

    /// <summary>
    /// Whether agents may propose file mutations at all. Off by default: reading is the useful part,
    /// and writing — even supervised — is the part that can lose data.
    /// </summary>
    public bool McpAllowWrites { get; set; } = false;

    /// <summary>
    /// Whether agents may read a server connection (SFTP, FTP, WebDAV) open in a panel through
    /// <c>panel://</c>. Off by default: a server has no path to check against
    /// <see cref="ApprovedRoots"/>, and an open connection is not consent to an agent reading it. An
    /// archive in a panel needs no switch: it is readable when its file lies inside an approved
    /// directory (ADR-0023).
    /// </summary>
    public bool McpAllowRemotePanels { get; set; } = false;

    /// <summary>
    /// Directories an agent may reach. Empty means nothing is reachable, so the server is useless
    /// until a root is approved — which is the intended first-run experience, not a bug.
    /// </summary>
    public List<string> ApprovedRoots { get; set; } = new();

    /// <summary>
    /// How long a write proposal waits for an answer before it is refused. A proposal that waited
    /// forever would hang the agent; one that auto-approved would defeat the whole mechanism.
    /// </summary>
    public int ApprovalTimeoutSeconds { get; set; } = 300;

    /// <summary>Start the server when the app starts, once <see cref="McpEnabled"/> is on.</summary>
    public bool McpAutoStart { get; set; } = true;

    // ── Language model (6.14–6.16, ADR-0007) ─────────────────────────────────

    /// <summary>Base URL of an OpenAI-compatible endpoint. Empty means no network calls, ever.</summary>
    public string LlmEndpoint { get; set; } = string.Empty;

    /// <summary>Bearer key; leave empty for a local server that wants none.</summary>
    /// <remarks>
    /// In memory this is the key itself; <c>settings.json</c> holds it DPAPI-protected (see
    /// <see cref="LlmApiKeyStored"/>), so the file can be copied, backed up or committed without
    /// leaking it. A key that cannot be decrypted on this account reads as empty.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public string LlmApiKey
    {
        get => SecretProtector.Unprotect(LlmApiKeyStored);
        set => LlmApiKeyStored = SecretProtector.Protect(value);
    }

    /// <summary>What is written to the file for <see cref="LlmApiKey"/>. Named as before, so an old plaintext value still loads.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("LlmApiKey")]
    public string LlmApiKeyStored { get; set; } = string.Empty;

    /// <summary>
    /// Re-protects a key an older build stored in the clear. Only the settings dialog's setter used to
    /// encrypt, so a plaintext key survived every ordinary save; called from <c>Normalize</c>, the next
    /// save of any kind writes it protected.
    /// </summary>
    public void ProtectLegacyApiKey()
    {
        LlmApiKeyStored ??= string.Empty;
        if (LlmApiKeyStored.Length > 0 && !SecretProtector.IsProtected(LlmApiKeyStored))
            LlmApiKey = LlmApiKeyStored;
    }

    /// <summary>Chat model id used for commit drafts, summaries and renaming.</summary>
    public string LlmModel { get; set; } = string.Empty;

    /// <summary>Embedding model id used by semantic search; may be the same endpoint.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    public int LlmMaxTokens { get; set; } = 800;

    /// <summary>
    /// How long one model call may take, in seconds; edited on the agent tab of the settings. The
    /// client keeps it within <see cref="TCV.Ai.Llm.LlmOptions.MinTimeoutSeconds"/>…<see
    /// cref="TCV.Ai.Llm.LlmOptions.MaxTimeoutSeconds"/> whatever the file says.
    /// </summary>
    public int LlmTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Host the user agreed to send file contents to for semantic indexing. Indexing embeds the text
    /// of every file, which leaves the machine unless the endpoint is local; the question is asked
    /// once per host and remembered here. Empty until answered.
    /// </summary>
    public string EmbeddingsConsentHost { get; set; } = string.Empty;

    /// <summary>
    /// Host the user agreed to send file excerpts to for AI renaming — the same once-per-host question
    /// as <see cref="EmbeddingsConsentHost"/>, asked before the first proposal. Empty until answered.
    /// </summary>
    public string AiRenameConsentHost { get; set; } = string.Empty;

    /// <summary>
    /// The port the MCP server last bound when <see cref="McpPort"/> is 0. Tried first on the next
    /// start, so the URL a client was registered with keeps working across restarts (the token still
    /// changes every run). 0 until a first start.
    /// </summary>
    public int McpLastPort { get; set; }
}

/// <summary>
/// Git-as-a-process settings: where worktrees are created and what a fresh one needs before it can
/// actually run. Both defaults are deliberately "empty means derive it" so a portable copy works
/// with no configuration at all.
/// </summary>
public sealed class GitSettings
{
    /// <summary>
    /// Directory new worktrees are created under. Empty derives <c>&lt;repo&gt;-worktrees</c> next to
    /// the repository, which keeps them out of the checkout (a worktree inside the repo shows up as
    /// untracked noise in every status).
    /// </summary>
    public string WorktreeRoot { get; set; } = string.Empty;

    /// <summary>
    /// Globs for untracked files a new worktree needs copied from the source checkout (<c>.env</c>
    /// and friends). Empty means "not customised" and falls back to
    /// <see cref="TCV.Git.WorktreeSetupPlan.DefaultCarryPatterns"/>.
    /// </summary>
    public List<string> WorktreeCarryPatterns { get; set; } = new();

    /// <summary>Command run in a freshly created worktree (<c>npm install</c>, <c>dotnet restore</c>).
    /// Empty runs nothing.</summary>
    public string WorktreeSetupCommand { get; set; } = string.Empty;

    /// <summary>Whether creating a worktree also registers and switches to a workspace for it.
    /// On by default: a worktree without a workspace is just another folder.</summary>
    public bool WorktreeCreatesWorkspace { get; set; } = true;
}

/// <summary>
/// Editors and agent CLIs offered by «Open in editor» (Ctrl+E) and «Run agent here» (Ctrl+Shift+A).
/// An empty list means "not customised": the app then offers whatever it detects from
/// <see cref="Navigation.ExternalToolCatalog"/>, so a fresh portable copy works without setup.
/// </summary>
public sealed class ExternalToolsSettings
{
    public List<ExternalTool> Editors { get; set; } = new();
    public List<ExternalTool> Agents { get; set; } = new();

    /// <summary>Name of the editor Ctrl+E uses. Empty = the first one detected.</summary>
    public string DefaultEditor { get; set; } = string.Empty;

    /// <summary>Name of the agent Ctrl+Shift+A uses. Empty = the first one detected.</summary>
    public string DefaultAgent { get; set; } = string.Empty;

    /// <summary>
    /// The program F4 edits a file in (Settings → Files → Editor). An empty command keeps Windows'
    /// own choice: the type's "edit" verb, else Notepad. Arguments without <c>%F</c> get the file
    /// appended (<see cref="Services.FileEditorLauncher.ToolFor"/>); the name is not used.
    /// </summary>
    public ExternalTool FileEditor { get; set; } = new();
}

/// <summary>
/// The shells the bottom command line runs a typed command in: cmd, PowerShell, Git Bash, WSL or any
/// other. An empty list means "not customised": the app offers what it finds on this machine
/// (<see cref="TCV.App.Shells.ShellCatalog"/>), as the editors list does, so a fresh portable copy works
/// without setup.
/// </summary>
public sealed class ShellSettings
{
    public List<ShellProfile> Profiles { get; set; } = new();

    /// <summary>Name of the shell the command line uses. Empty, or a shell no longer offered, = the first one.</summary>
    public string Selected { get; set; } = string.Empty;
}

/// <summary>One shell of the command line.</summary>
public sealed class ShellProfile
{
    /// <summary>Shown in the command line's picker; also the key <see cref="ShellSettings.Selected"/> stores.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The program: a name looked up on PATH, or a path. Environment variables are expanded.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Argument template: <c>%C</c> the typed command as one argument (appended when the
    /// template has none), <c>%P</c> the panel's folder.</summary>
    public string Arguments { get; set; } = string.Empty;
}

/// <summary>One launchable external program.</summary>
public sealed class ExternalTool
{
    /// <summary>Shown in the picker and stored as the "default" key.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Executable or shim name (resolved through PATH) or an absolute path.</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Argument template: <c>%P</c> panel directory · <c>%F</c> selected paths ·
    /// <c>%N</c> name without extension · <c>%E</c> extension.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Run inside a terminal window — required for TUI programs (agent CLIs, nvim).</summary>
    public bool InTerminal { get; set; }
}

/// <summary>
/// The main window's restored rectangle and whether it was maximised, as Windows reports them
/// (<c>GetWindowPlacement</c>): physical pixels in work-area coordinates. Handing them back through
/// <c>SetWindowPlacement</c> puts the window on the monitor it was on, keeps the restored size of a
/// maximised window, and moves a window whose monitor is gone back onto the screen.
/// </summary>
public sealed class WindowPlacementSettings
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public bool Maximized { get; set; }

    /// <summary>
    /// Worth restoring: a real rectangle, no smaller than the window allows and no larger than a wall of
    /// monitors. A hand-edited or half-written file falls back to the design size instead.
    /// </summary>
    public bool IsUsable(int minWidth, int minHeight)
    {
        const int Largest = 32_000;
        var width = (long)Right - Left;
        var height = (long)Bottom - Top;
        return width >= minWidth && height >= minHeight && width <= Largest && height <= Largest
            && Math.Abs((long)Left) <= Largest && Math.Abs((long)Top) <= Largest;
    }
}

public sealed class UpdateSettings
{
    /// <summary>
    /// GitHub repository that publishes releases, as "owner/name"; a folder or URL instead is a
    /// Velopack feed, for self-hosting and for trying an update locally (docs/RELEASE.md). Empty
    /// means "not customised" — UpdateService falls back to its own default, so a repo rename is a
    /// one-line change there rather than a second copy that stale settings.json files would keep
    /// pinned to the old name.
    /// </summary>
    public string Repository { get; set; } = string.Empty;
}

public sealed class Bookmark
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
}

public sealed class PanelSettings
{
    public string LastPath { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public ViewMode ViewMode { get; set; } = ViewMode.Details;
    public SortColumn SortBy { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; } = false;
    public bool ShowHiddenFiles { get; set; } = false;
    public bool FoldersFirst { get; set; } = true;
    public bool ShowExtensions { get; set; } = true;
    public List<TabState> Tabs { get; set; } = new();

    /// <summary>Index into <see cref="Tabs"/> of the tab that was showing at exit.</summary>
    public int ActiveTabIndex { get; set; }

    /// <summary>
    /// Columns of the details view, in display order, with their widths (6.19). Empty means
    /// "not customised" and falls back to <see cref="Columns.ColumnCatalog.DefaultLayout"/>, so an
    /// old <c>settings.json</c> keeps working.
    /// </summary>
    public List<ColumnLayout> Columns { get; set; } = new();

    /// <summary>
    /// Hide files the repository's <c>.gitignore</c> excludes (6.20). Off by default: a file
    /// manager that silently omits files would be a bad default for anyone outside a working copy.
    /// </summary>
    public bool HideIgnored { get; set; } = false;
}

/// <summary>One column's place in the details view: which column, how wide, shown or not.</summary>
public sealed class ColumnLayout
{
    /// <summary>Id from <see cref="Columns.ColumnCatalog"/>; unknown ids are dropped on load.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Width in pixels; 0 means the star-sized name column.</summary>
    public double Width { get; set; }

    public bool Visible { get; set; } = true;
}

public sealed class TabState
{
    public string Path { get; set; } = string.Empty;
    public string? CustomTitle { get; set; }
    public bool IsLocked { get; set; }
}

public sealed class OperationSettings
{
    /// <summary>
    /// Ask before deleting. Only honoured for a delete that goes to the Recycle Bin (it can be
    /// undone); a permanent delete always asks (INV-11).
    /// </summary>
    public bool ConfirmDelete { get; set; } = true;

    public bool MoveToRecycleBin { get; set; } = true;

    /// <summary>
    /// Copy and move ask what to do about a file that already exists. Off makes the copy dialog
    /// start on "overwrite" instead of "ask".
    /// </summary>
    public bool ConfirmOverwrite { get; set; } = true;

    /// <summary>Not used: each lane of the operation queue runs one operation at a time by design (ADR-0038). Kept so old settings.json files load.</summary>
    public int MaxParallelOperations { get; set; } = 1;
}

public sealed class ThemeSettings
{
    public string ThemeName { get; set; } = "Dark";
    public bool UseSystemTheme { get; set; } = false;
    /// <summary>UI language code: "en" or "ru". Empty = detect from system.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Accent colour as <c>#RRGGBB</c>; empty keeps the theme's own. Parsed as forgivingly as a
    /// workspace colour (<see cref="Workspaces.WorkspaceColor"/>), and an unparsable hand-edited
    /// value is ignored rather than painted.
    /// </summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>Font family of the file lists; empty keeps the default (the UI font, see Themes/Base.xaml).</summary>
    public string PanelFontFamily { get; set; } = string.Empty;

    /// <summary>Font size of the file lists, clamped by <see cref="AppearanceResources.ClampFontSize"/>.</summary>
    public double PanelFontSize { get; set; } = AppearanceResources.DefaultPanelFontSize;

    /// <summary>
    /// The file lists draw Windows' own icons, as Explorer does, instead of the design's line icons in
    /// the theme's colours. Off by default: the line icons are the design (ADR-0030).
    /// </summary>
    public bool SystemFileIcons { get; set; } = false;
}

/// <summary>
/// The parts of the window chrome that can be hidden (6.33). Each one only takes space: the commands
/// behind a hidden element keep working from the keyboard and the command palette.
/// </summary>
public sealed class LayoutSettings
{
    /// <summary>The F-key bar along the bottom of the window.</summary>
    public bool ShowFunctionKeyBar { get; set; } = true;

    /// <summary>The drive selector above each panel.</summary>
    public bool ShowDriveBar { get; set; } = true;

    /// <summary>The tab strip of each panel.</summary>
    public bool ShowTabBar { get; set; } = true;

    /// <summary>The breadcrumb path with the Back / Forward buttons.</summary>
    public bool ShowPathBar { get; set; } = true;

    /// <summary>The view-mode and display-toggle buttons in each panel's status line.</summary>
    public bool ShowViewToolbar { get; set; } = true;

    /// <summary>The folders-and-files line under each panel.</summary>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>The tool strip under the menu (tree, layout, search, branch, commit, agent changes, theme).</summary>
    public bool ShowToolbar { get; set; } = true;

    /// <summary>The command line above the F-key bar.</summary>
    public bool ShowCommandLine { get; set; } = true;

    /// <summary>The window's status line at the very bottom (project, branch, operations, AI, MCP).</summary>
    public bool ShowAppStatusBar { get; set; } = true;
}

public sealed class ColorRule
{
    public string Pattern { get; set; } = string.Empty;
    public string Foreground { get; set; } = string.Empty;
    public string Background { get; set; } = string.Empty;
    public bool ApplyToDirectories { get; set; } = false;
}

public enum ViewMode { Details, Brief, Thumbnails, Tree }

public enum SortColumn { Name, Extension, Size, Date, Attributes }
