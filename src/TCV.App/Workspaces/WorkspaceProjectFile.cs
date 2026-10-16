using System.IO;
using System.Text.Json;

namespace TCV.App.Workspaces;

/// <summary>
/// The committable half of a workspace (ADR-0011). Holds only what makes sense to share with
/// everyone who clones the repo — roots, identity, how to start the project — and none of the
/// volatile session state (tab sets, focus, splitter) that lives in <c>workspaces.json</c>.
/// Paths are stored relative to the <c>.tcvproj</c> file, so the same file works from any checkout
/// directory on any machine.
/// </summary>
public sealed class WorkspaceDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;

    /// <summary>Left panel root, relative to the project file.</summary>
    public string LeftRoot { get; set; } = string.Empty;

    /// <summary>Right panel root, relative to the project file.</summary>
    public string RightRoot { get; set; } = string.Empty;

    public bool SplitVertical { get; set; }
    public bool ActiveLeft { get; set; } = true;

    public string TerminalCommand { get; set; } = string.Empty;

    /// <summary>Terminal working directory, relative to the project file.</summary>
    public string TerminalDirectory { get; set; } = string.Empty;
}

/// <summary>Reads and writes <c>*.tcvproj</c> and converts to and from the session model.</summary>
public static class WorkspaceProjectFile
{
    public const string Extension = ".tcvproj";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads a definition; returns null when the file is missing or not valid JSON —
    /// a hand-edited committed file must not crash the app that opens it.</summary>
    public static WorkspaceDefinition? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<WorkspaceDefinition>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, WorkspaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        File.WriteAllText(path, JsonSerializer.Serialize(definition, JsonOptions));
    }

    /// <summary>
    /// Projects a session workspace down to its committable definition. Each panel contributes the
    /// path of its active tab — the definition records where the project starts, not every tab the
    /// author happened to have open.
    /// </summary>
    public static WorkspaceDefinition FromWorkspace(Workspace workspace, string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var baseDir = BaseDirectoryOf(projectFilePath);

        return new WorkspaceDefinition
        {
            Name = workspace.Name,
            Icon = workspace.Icon,
            Color = workspace.Color,
            Group = workspace.Group,
            LeftRoot = ToRelative(ActivePathOf(workspace.Left), baseDir),
            RightRoot = ToRelative(ActivePathOf(workspace.Right), baseDir),
            SplitVertical = workspace.SplitVertical,
            ActiveLeft = workspace.ActiveLeft,
            TerminalCommand = workspace.TerminalCommand,
            TerminalDirectory = ToRelative(workspace.TerminalDirectory, baseDir),
        };
    }

    /// <summary>
    /// Expands a definition into a session workspace rooted at the project file's directory: one
    /// tab per panel, pointing at the resolved roots.
    /// </summary>
    public static Workspace ToWorkspace(WorkspaceDefinition definition, string projectFilePath)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var baseDir = BaseDirectoryOf(projectFilePath);

        // An unnamed definition still has to become a usable workspace — fall back to the file name.
        var name = string.IsNullOrWhiteSpace(definition.Name)
            ? Path.GetFileNameWithoutExtension(projectFilePath)
            : definition.Name.Trim();

        return new Workspace
        {
            Name = name,
            Icon = definition.Icon,
            Color = definition.Color,
            Group = definition.Group,
            Left = PanelFor(definition.LeftRoot, baseDir),
            Right = PanelFor(definition.RightRoot, baseDir),
            SplitVertical = definition.SplitVertical,
            ActiveLeft = definition.ActiveLeft,
            TerminalCommand = definition.TerminalCommand,
            TerminalDirectory = ToAbsolute(definition.TerminalDirectory, baseDir),
            LastUsedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Applies a freshly opened definition to the workspace that already exists in the session
    /// store, in place. The file owns the shared half (identity, roots, how the project is started);
    /// the local half — the rest of the tab set, view/sort state and pinning — is kept, because
    /// overwriting the stored entry outright would throw away exactly what ADR-0011 keeps local.
    /// </summary>
    public static Workspace MergeIntoSession(Workspace fromDefinition, Workspace session)
    {
        ArgumentNullException.ThrowIfNull(fromDefinition);
        ArgumentNullException.ThrowIfNull(session);

        session.Icon = fromDefinition.Icon;
        session.Color = fromDefinition.Color;
        session.Group = fromDefinition.Group;
        session.SplitVertical = fromDefinition.SplitVertical;
        session.ActiveLeft = fromDefinition.ActiveLeft;
        session.TerminalCommand = fromDefinition.TerminalCommand;
        session.TerminalDirectory = fromDefinition.TerminalDirectory;
        session.LastUsedUtc = fromDefinition.LastUsedUtc;

        MergeRootInto(fromDefinition.Left, session.Left);
        MergeRootInto(fromDefinition.Right, session.Right);
        return session;
    }

    /// <summary>
    /// True when the stored workspace already opens one of the folders the definition names — the
    /// same project opened again. False means a different project that merely shares the name, and
    /// merging into it would rewrite someone else's workspace.
    /// </summary>
    public static bool SharesRoot(Workspace fromDefinition, Workspace session)
    {
        ArgumentNullException.ThrowIfNull(fromDefinition);
        ArgumentNullException.ThrowIfNull(session);

        var roots = new[] { ActivePathOf(fromDefinition.Left), ActivePathOf(fromDefinition.Right) }
            .Select(NormalizeForCompare)
            .Where(p => p.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (roots.Count == 0) return false;

        return session.Left.Tabs.Concat(session.Right.Tabs)
            .Any(t => roots.Contains(NormalizeForCompare(t.Path)));
    }

    private static string NormalizeForCompare(string? path) =>
        (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// Makes the definition's root the panel's active tab: reused if that folder is already open,
    /// added in front otherwise. The other tabs stay — a project file names where work starts, it
    /// does not claim the tabs are wrong.
    /// </summary>
    private static void MergeRootInto(WorkspacePanelState fromDefinition, WorkspacePanelState session)
    {
        var root = ActivePathOf(fromDefinition);
        if (root.Length == 0) return;   // the definition names no root for this panel — keep the tabs

        var index = session.Tabs.FindIndex(
            t => string.Equals(t.Path, root, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            session.Tabs.Insert(0, new Settings.TabState { Path = root });
            index = 0;
        }

        session.ActiveTabIndex = index;
    }

    private static WorkspacePanelState PanelFor(string relativeRoot, string baseDir)
    {
        var path = ToAbsolute(relativeRoot, baseDir);
        var state = new WorkspacePanelState { ActiveTabIndex = 0 };
        if (path.Length > 0)
            state.Tabs.Add(new Settings.TabState { Path = path });
        return state;
    }

    private static string ActivePathOf(WorkspacePanelState panel)
    {
        if (panel.Tabs.Count == 0) return string.Empty;
        var index = Math.Clamp(panel.ActiveTabIndex, 0, panel.Tabs.Count - 1);
        return panel.Tabs[index].Path ?? string.Empty;
    }

    /// <summary>The directory a project file's relative paths are anchored to.</summary>
    private static string BaseDirectoryOf(string projectFilePath) =>
        Path.GetDirectoryName(Path.GetFullPath(projectFilePath)) ?? AppContext.BaseDirectory;

    /// <summary>
    /// Makes a path relative to the project file and writes it with forward slashes — the file is
    /// meant to be read in a diff. A path on another drive cannot be relativised at all, so it is
    /// kept absolute rather than silently rewritten into something that resolves elsewhere.
    /// </summary>
    private static string ToRelative(string? absolute, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(absolute)) return string.Empty;

        try
        {
            var relative = Path.GetRelativePath(baseDir, absolute);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return absolute.Replace('\\', '/');
        }
    }

    /// <summary>Resolves a stored path back against the project file's directory. Already-absolute
    /// values (another drive, a UNC share) pass through untouched.</summary>
    private static string ToAbsolute(string? stored, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(stored)) return string.Empty;

        var native = stored.Replace('/', Path.DirectorySeparatorChar);
        try
        {
            return Path.GetFullPath(Path.IsPathRooted(native) ? native : Path.Combine(baseDir, native));
        }
        catch
        {
            return native;
        }
    }
}
