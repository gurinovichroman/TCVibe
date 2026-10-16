using System.IO;
using TCV.Git;

namespace TCV.App.Workspaces;

/// <summary>
/// A folder dropped on the workspace rail becomes a workspace in it (6.79). The decisions that do not
/// need a window: what the drop carries, what the new workspace is called, and whether some workspace
/// already stands in that folder — then the drop switches to it instead of making a second one.
/// </summary>
public static class WorkspaceFolderDrop
{
    /// <summary>
    /// The one full path a file drop carries, or null for none, several, or a relative one. Several
    /// folders would be several workspaces behind one properties window; a drop from an archive or a
    /// server carries no paths at all, so it never gets here.
    /// </summary>
    public static string? SinglePath(string[]? paths) =>
        paths is [var path] && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? path
            : null;

    /// <summary>
    /// The name a workspace in <paramref name="folder"/> starts with: the folder's name — for a root,
    /// its last part (<c>D:</c>, a share's name) — numbered «(2)», «(3)»… when a workspace already has it.
    /// </summary>
    public static string NameFor(string folder, IEnumerable<string> taken)
    {
        var trimmed = Normalize(folder);
        var name = Path.GetFileName(trimmed);
        if (name.Length == 0)
        {
            name = trimmed.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? trimmed;
        }

        return GitWorktreePaths.UniqueName(name, taken);
    }

    /// <summary>
    /// The first workspace (in the order given) that stands in <paramref name="folder"/>: the active
    /// tab of its left or right panel is that folder — the same "root" a <c>*.tcvproj</c> records.
    /// Any other tab does not count: a Downloads tab kept open in a project does not make the project
    /// the workspace of Downloads.
    /// </summary>
    public static Workspace? RootedAt(IEnumerable<Workspace> workspaces, string folder)
    {
        var wanted = Normalize(folder);
        if (wanted.Length == 0)
        {
            return null;
        }

        return workspaces.FirstOrDefault(w =>
            string.Equals(Normalize(ActivePath(w.Left)), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Normalize(ActivePath(w.Right)), wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static string ActivePath(WorkspacePanelState panel) =>
        panel.Tabs.Count == 0
            ? string.Empty
            : panel.Tabs[Math.Clamp(panel.ActiveTabIndex, 0, panel.Tabs.Count - 1)].Path ?? string.Empty;

    private static string Normalize(string? path) =>
        (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\');
}
