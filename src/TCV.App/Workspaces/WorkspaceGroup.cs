namespace TCV.App.Workspaces;

/// <summary>One heading on the workspace rail plus the pinned workspaces filed under it.</summary>
public sealed class WorkspaceGroup
{
    public WorkspaceGroup(string header, IReadOnlyList<Workspace> items)
    {
        Header = header;
        Items = items;
    }

    /// <summary>Group name; empty for the ungrouped block that leads the list.</summary>
    public string Header { get; }

    /// <summary>False for the ungrouped block, which renders without a heading.</summary>
    public bool HasHeader => !string.IsNullOrWhiteSpace(Header);

    /// <summary>
    /// The heading as the rail prints it: in capitals, like the side bar's section labels — WPF has no
    /// text-transform, and the dictionaries can capitalise only their own words.
    /// </summary>
    public string Label => Header.ToUpper(System.Globalization.CultureInfo.CurrentCulture);

    public IReadOnlyList<Workspace> Items { get; }
}

/// <summary>
/// Builds the rail's grouped view. Kept separate from the view model so the ordering rules —
/// the ones a user notices immediately when they are wrong — are testable without a window.
/// </summary>
public static class WorkspaceGrouping
{
    /// <summary>
    /// Pinned workspaces only, ungrouped block first, then groups alphabetically. Order inside a
    /// group is the caller's. The caller's list is <see cref="WorkspaceOrder.Arrange"/>d, which puts
    /// the blocks in this same order, so the rail top to bottom is the Alt+N numbering (6.79).
    /// </summary>
    public static IReadOnlyList<WorkspaceGroup> BuildPinnedGroups(IEnumerable<Workspace>? workspaces)
    {
        if (workspaces is null) return Array.Empty<WorkspaceGroup>();

        var groups = workspaces
            .Where(w => w.Pinned)
            .GroupBy(w => (w.Group ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new WorkspaceGroup(g.Key, g.ToList()))
            // Ungrouped leads: it is the default state, and burying it under named groups would
            // make pinning feel like it also requires naming a group.
            .OrderBy(g => g.HasHeader)
            .ThenBy(g => g.Header, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups;
    }

    /// <summary>
    /// The workspace the panels are on when it is not pinned, else null. The rail shows it after the
    /// pinned ones, the way the taskbar shows a running program that is not pinned: the rail never
    /// hides where you are, and an unpinned workspace is told apart by where it sits instead of
    /// by a checkmark in its properties.
    /// </summary>
    public static Workspace? UnpinnedActive(IEnumerable<Workspace>? workspaces, string? activeName)
    {
        if (workspaces is null || string.IsNullOrEmpty(activeName)) return null;

        return workspaces.FirstOrDefault(
            w => !w.Pinned && string.Equals(w.Name, activeName, StringComparison.OrdinalIgnoreCase));
    }
}
