namespace TCV.App.Workspaces;

/// <summary>
/// The one order every list of workspaces follows (6.79): the rail top to bottom, the Workspaces
/// menu, the list under the title bar, the switcher and Alt+1..9. Pinned workspaces lead, in the
/// rail's blocks — the ungrouped ones, then each group A→Z — and inside a block in the order the
/// user dragged them into (<see cref="Workspace.Order"/>). The rest follow, most recently used first.
/// </summary>
/// <remarks>
/// A drag moves a workspace only inside its own block: the group is a property (changed in the
/// properties window), not a place, and a block that a drag could leave would stop being one heading.
/// A list of workspaces put together without <see cref="Arrange"/> numbers Alt+N differently from the
/// rail (TRAP-83).
/// </remarks>
public static class WorkspaceOrder
{
    /// <summary>The group a workspace is filed under, as the rail compares it (trimmed, any case).</summary>
    public static string GroupKey(Workspace workspace) => (workspace.Group ?? string.Empty).Trim();

    /// <summary>True when both are pinned under the same heading — the only moves a drag makes.</summary>
    public static bool SameBlock(Workspace a, Workspace b) =>
        a.Pinned && b.Pinned && string.Equals(GroupKey(a), GroupKey(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts <paramref name="workspaces"/> in the order described above. <paramref name="shown"/> is the
    /// list on screen: where nothing else decides (the MRU-ordered rest, workspaces not placed yet), an
    /// entry keeps its place in it, so editing an icon after a switch does not move the Alt+N numbers
    /// under the user's hands. Entries not shown go after the shown ones, most recently used first.
    /// </summary>
    public static IReadOnlyList<Workspace> Arrange(
        IEnumerable<Workspace> workspaces, IReadOnlyList<Workspace>? shown = null)
    {
        var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < (shown?.Count ?? 0); i++)
        {
            position.TryAdd(shown![i].Name, i);
        }

        int Shown(Workspace w) => position.TryGetValue(w.Name, out var i) ? i : int.MaxValue;

        var all = workspaces.ToList();
        var pinned = all.Where(w => w.Pinned)
            // Ungrouped leads: it is the default state, and burying it under named groups would make
            // pinning feel like it also requires naming a group.
            .OrderBy(w => GroupKey(w).Length > 0)
            .ThenBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(w => w.Order > 0 ? 0 : 1)
            .ThenBy(w => w.Order)
            .ThenBy(Shown)
            .ThenByDescending(w => w.LastUsedUtc)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase);
        var rest = all.Where(w => !w.Pinned)
            .OrderBy(Shown)
            .ThenByDescending(w => w.LastUsedUtc)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase);

        return pinned.Concat(rest).ToList();
    }

    /// <summary>
    /// True when the pinned workspaces of an <see cref="Arrange"/>d list do not carry rising numbers
    /// yet — a file from before 6.79, a workspace just pinned or regrouped. Until they do, their order
    /// leans on the MRU stamp and would change on the next launch.
    /// </summary>
    public static bool NeedsNumbering(IReadOnlyList<Workspace> arranged)
    {
        var last = 0;
        foreach (var workspace in arranged.Where(w => w.Pinned))
        {
            if (workspace.Order <= last)
            {
                return true;
            }

            last = workspace.Order;
        }

        return false;
    }

    /// <summary>
    /// Numbers the pinned workspaces 1, 2, 3… in the order given and returns their names in that
    /// order, which is what <see cref="WorkspaceStore.SetOrder"/> writes.
    /// </summary>
    public static IReadOnlyList<string> Number(IReadOnlyList<Workspace> arranged)
    {
        var names = new List<string>();
        foreach (var workspace in arranged.Where(w => w.Pinned))
        {
            names.Add(workspace.Name);
            workspace.Order = names.Count;
        }

        return names;
    }

    /// <summary>The pinned workspaces under the same heading as <paramref name="workspace"/>, in order.</summary>
    public static IReadOnlyList<Workspace> BlockOf(IReadOnlyList<Workspace> arranged, Workspace workspace) =>
        arranged.Where(w => SameBlock(w, workspace)).ToList();

    /// <summary>
    /// Moves the pinned workspace <paramref name="name"/> to <paramref name="slot"/> of its block in an
    /// <see cref="Arrange"/>d list — 0 is before the first, the block's count after the last — and
    /// returns the names of all pinned workspaces in their new order. Null when nothing moves: no such
    /// pinned workspace, a slot outside the block, or the slot on either side of where it already is.
    /// </summary>
    public static IReadOnlyList<string>? Move(IReadOnlyList<Workspace> arranged, string name, int slot)
    {
        // Arranged again against itself (which keeps an arranged list as it is), so that a block is
        // contiguous even in a list that was patched by hand.
        var pinned = Arrange(arranged, arranged).Where(w => w.Pinned).ToList();
        var moved = pinned.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (moved is null)
        {
            return null;
        }

        var block = BlockOf(pinned, moved).ToList();
        var from = block.IndexOf(moved);
        if (slot < 0 || slot > block.Count || slot == from || slot == from + 1)
        {
            return null;
        }

        // A block is contiguous, so the new block replaces the old one in place.
        var start = pinned.IndexOf(block[0]);
        block.RemoveAt(from);
        block.Insert(slot > from ? slot - 1 : slot, moved);
        pinned.RemoveRange(start, block.Count);
        pinned.InsertRange(start, block);
        return pinned.Select(w => w.Name).ToList();
    }

    /// <summary>
    /// Which gap of a block a point falls into while a workspace is dragged over the rail. The
    /// <paramref name="items"/> are the block's buttons top to bottom; a point above a button's middle
    /// is a place before it. Null when the point is more than <paramref name="reach"/> above the first
    /// button or below the last — over another group, which is not a place in this one.
    /// </summary>
    public static int? SlotAt(double y, IReadOnlyList<(double Top, double Bottom)> items, double reach)
    {
        if (items.Count == 0 || y < items[0].Top - reach || y > items[^1].Bottom + reach)
        {
            return null;
        }

        var slot = 0;
        while (slot < items.Count && y > (items[slot].Top + items[slot].Bottom) / 2)
        {
            slot++;
        }

        return slot;
    }
}
