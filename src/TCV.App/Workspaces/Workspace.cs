using TCV.App.Settings;

namespace TCV.App.Workspaces;

/// <summary>
/// Snapshot of one panel inside a workspace: the full tab list (not just the active tab), which
/// tab was active, and the view state (mode/sort/filters). Mirrors the fields of
/// <see cref="PanelSettings"/> plus <see cref="ActiveTabIndex"/>.
/// </summary>
public sealed class WorkspacePanelState
{
    public List<TabState> Tabs { get; set; } = new();
    public int ActiveTabIndex { get; set; }
    public ViewMode ViewMode { get; set; } = ViewMode.Details;
    public SortColumn SortBy { get; set; } = SortColumn.Name;
    public bool SortDescending { get; set; }
    public bool ShowHiddenFiles { get; set; }
    public bool FoldersFirst { get; set; } = true;
    public bool ShowExtensions { get; set; } = true;

    /// <summary>
    /// A panel with nothing in it yet: one tab on <paramref name="folder"/>. The view (mode, sort,
    /// hidden files…) is carried over from <paramref name="view"/> — that is how the user likes to
    /// look at files, not part of the project being left behind.
    /// </summary>
    public static WorkspacePanelState Blank(string folder, WorkspacePanelState? view)
    {
        var state = new WorkspacePanelState { Tabs = [new TabState { Path = folder }] };
        if (view is not null)
        {
            state.ViewMode = view.ViewMode;
            state.SortBy = view.SortBy;
            state.SortDescending = view.SortDescending;
            state.ShowHiddenFiles = view.ShowHiddenFiles;
            state.FoldersFirst = view.FoldersFirst;
            state.ShowExtensions = view.ShowExtensions;
        }

        return state;
    }
}

/// <summary>
/// A named, switchable bundle of the whole two-panel context: both panels' full tab layouts and
/// view state, which panel was active, the split orientation, plus an optional colour/icon so the
/// user recognises the project at a glance. This is the unit that beats plain tabs+bookmarks —
/// it restores <em>what you were doing</em>, not just where one file was.
/// </summary>
public sealed class Workspace
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Optional accent colour (hex, e.g. "#4F9CF9") shown on the workspace.</summary>
    public string Color { get; set; } = string.Empty;
    /// <summary>Optional emoji/character shown before the name.</summary>
    public string Icon { get; set; } = string.Empty;

    /// <summary>
    /// Optional picture of the project on the rail, in place of the emoji or the letters: the file name
    /// of a 64 px PNG in <see cref="WorkspaceIconStore"/> (a copy, so moving the repository or editing
    /// its logo does not change it). Empty — no picture.
    /// </summary>
    public string IconImage { get; set; } = string.Empty;

    /// <summary>
    /// The picture may be looked for in the project's repository (<see cref="ProjectIconFinder"/>) while
    /// the workspace has neither a picture nor an emoji. Cleared once the user chooses — a picture, an
    /// emoji or none — so a removed logo does not come back on the next start.
    /// </summary>
    public bool AutoIconImage { get; set; } = true;
    public WorkspacePanelState Left { get; set; } = new();
    public WorkspacePanelState Right { get; set; } = new();
    /// <summary>Which panel had focus (true = left).</summary>
    public bool ActiveLeft { get; set; } = true;
    public bool SplitVertical { get; set; }
    public bool Pinned { get; set; }

    /// <summary>
    /// Optional heading the workspace rail files a pinned workspace under ("Work", "OSS"…). Empty
    /// means ungrouped, and those are listed first — a flat list must not need a group to exist.
    /// </summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Place of a pinned workspace in its block on the rail, where the user dragged it (6.79): the
    /// rail, the menus and Alt+1..9 follow it (<see cref="WorkspaceOrder"/>). 0 — not placed yet (a
    /// file from before, a workspace just pinned or moved to another group): it goes after the placed
    /// ones, and the next reload numbers it.
    /// </summary>
    public int Order { get; set; }

    /// <summary>UTC timestamp of the last switch-to, for MRU ordering.</summary>
    public DateTime LastUsedUtc { get; set; }

    /// <summary>
    /// Command line this project is normally driven with (<c>npm run dev</c>, <c>dotnet watch</c>,
    /// an agent CLI…), replayed by the "run workspace terminal" command and set in the properties
    /// window. Empty means "just open a shell" — the saved command is a convenience, not a requirement.
    /// </summary>
    public string TerminalCommand { get; set; } = string.Empty;

    /// <summary>
    /// Working directory for <see cref="TerminalCommand"/>. Empty falls back to the folder of the
    /// workspace's active panel (the live one while it is open, the saved one otherwise), so a
    /// workspace stays useful before its cwd has ever been pinned down.
    /// </summary>
    public string TerminalDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The command line the user has typed or explicitly approved on this machine. A command that
    /// arrives from a committed <c>*.tcvproj</c> is somebody else's text, so it is not run until it
    /// has been shown to the user and this field matches it. Session-local: it never travels in the
    /// project file, and an imported bundle starts with it cleared.
    /// </summary>
    public string TrustedTerminalCommand { get; set; } = string.Empty;

    /// <summary>True when there is a saved command the user has not yet approved.</summary>
    public bool NeedsTerminalConfirmation() =>
        !string.IsNullOrWhiteSpace(TerminalCommand)
        && !string.Equals(TerminalCommand.Trim(), TrustedTerminalCommand?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Takes the terminal setup as edited in the properties window. Only a command the user changed
    /// there counts as typed by them: pressing OK on a command that came from a project file must not
    /// be enough to run it unseen. The window hands its text back trimmed, and a project file's need not
    /// be, so the two are compared trimmed: a trailing space or line break is not an edit.
    /// </summary>
    public void EditTerminal(string command, string directory)
    {
        if (!string.Equals(command.Trim(), TerminalCommand?.Trim(), StringComparison.Ordinal))
        {
            TrustedTerminalCommand = command;
        }

        TerminalCommand = command;
        TerminalDirectory = directory;
    }

    /// <summary>
    /// Copies everything that belongs to the workspace itself rather than to a captured layout:
    /// appearance, how it is filed, and the terminal setup. Re-saving the panels under an existing
    /// name goes through here, so a field added to the workspace survives the overwrite by being
    /// listed once instead of at every capture site.
    /// </summary>
    public void CopyIdentityFrom(Workspace? source)
    {
        if (source is null)
        {
            return;
        }

        Color = source.Color;
        Icon = source.Icon;
        IconImage = source.IconImage;
        AutoIconImage = source.AutoIconImage;
        Group = source.Group;
        Pinned = source.Pinned;
        Order = source.Order;
        TerminalCommand = source.TerminalCommand;
        TerminalDirectory = source.TerminalDirectory;
        TrustedTerminalCommand = source.TrustedTerminalCommand;
    }

    /// <summary>
    /// Copies the captured layout (both panels, focus, split) and nothing else — the counterpart of
    /// <see cref="CopyIdentityFrom"/>. Lets the in-memory entry follow an autosave without being
    /// replaced, which would reorder the list.
    /// </summary>
    public void CopyLayoutFrom(Workspace source)
    {
        ArgumentNullException.ThrowIfNull(source);

        Left = source.Left;
        Right = source.Right;
        ActiveLeft = source.ActiveLeft;
        SplitVertical = source.SplitVertical;
    }

    /// <summary>
    /// "Project 1", "Project 2"… — the first number whose name no workspace has.
    /// <paramref name="format"/> is the localised pattern with <c>{0}</c> for the number.
    /// </summary>
    public static string NextNumberedName(string format, IEnumerable<string> taken)
    {
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        var names = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A pattern without the number (a missing resource reads "[Ws_DefaultName]") would give every
        // candidate the same name and never stop once that name is taken: number it after the text.
        if (string.Format(culture, format, 1) == string.Format(culture, format, 2))
        {
            format = format.Replace("{", "{{").Replace("}", "}}") + " {0}";
        }

        for (var n = 1; ; n++)
        {
            var name = string.Format(culture, format, n);
            if (!names.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>Name with the icon prefixed, for menus and the switcher.</summary>
    public string DisplayName =>
        string.IsNullOrWhiteSpace(Icon) ? Name : $"{Icon} {Name}";

    /// <summary>
    /// Icons offered with one click in the properties dialog; any other emoji or character can still
    /// be typed or pasted. WPF draws them in one colour, the window title in full colour.
    /// </summary>
    public static IReadOnlyList<string> SuggestedIcons { get; } =
    [
        "🚀", "🔧", "🐞", "🧪", "📦", "🌐", "🤖", "🔥", "⭐", "💼", "🏠", "📝", "🎮", "⚙", "⚡", "☕",
    ];
}
