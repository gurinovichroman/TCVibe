namespace TCV.App.Controls;

/// <summary>Why the list is putting the keyboard on its cursor, which decides whom it may take it from.</summary>
public enum FocusClaim
{
    /// <summary>It is handed the keys (<see cref="FileListControl.FocusList"/>, a view button): it takes them from anywhere.</summary>
    Hand,

    /// <summary>The panel became active or changed its view: from anywhere but the panel's own quick filter
    /// box, which a click into the box of the other panel has just made active.</summary>
    Panel,

    /// <summary>Code moved the cursor or refilled the rows: not from a box being typed into — a name being
    /// renamed, a quick filter. The box of the filter lost its keys on every letter that moved the cursor.
    /// And only in the active panel: the other panel reloaded by its watcher took the keys, and the next
    /// Shift+↓ marked a file over there.</summary>
    Follow,

    /// <summary>The same folder was read again (the watcher, the end of an operation) and the cursor put back
    /// on its rows: only keys the list held or that went nowhere. In a folder that keeps changing (%TEMP%)
    /// the command line lost its keys every few seconds.</summary>
    Refresh,
}

/// <summary>What holds the keyboard when the list wants it.</summary>
public enum KeyHolder
{
    /// <summary>Anything else outside the list: the command line, the path being typed, the folder tree.</summary>
    Other,

    /// <summary>The list itself, or nothing at all: a row the refill took away, the bare window.</summary>
    List,

    /// <summary>The box of a name being renamed in place (TRAP-76).</summary>
    NameEdit,

    /// <summary>The quick filter box of this panel.</summary>
    OwnFilter,

    /// <summary>The quick filter box of the other panel.</summary>
    OtherFilter,

    /// <summary>An open menu or drop-down: a menu item has the keys, or something outside the list holds
    /// the mouse (menu mode, a context menu, a combo box list). Focus taken from it closes it.</summary>
    Menu,
}

/// <summary>
/// Whether the file list may put the keyboard on its cursor. WPF-free, so the rule can be tested; the
/// control says who holds the keys (<see cref="KeyHolder"/>).
/// </summary>
public static class ListFocus
{
    public static bool MayTakeKeys(KeyHolder holder, FocusClaim claim, bool panelIsActive)
    {
        if (claim is FocusClaim.Follow or FocusClaim.Refresh && !panelIsActive)
        {
            return false;
        }

        return holder switch
        {
            KeyHolder.List => true,
            KeyHolder.NameEdit => false,
            KeyHolder.OwnFilter or KeyHolder.Menu => claim == FocusClaim.Hand,
            KeyHolder.OtherFilter => claim is FocusClaim.Hand or FocusClaim.Panel,
            _ => claim != FocusClaim.Refresh,
        };
    }
}
