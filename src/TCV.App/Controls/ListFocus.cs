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
}

/// <summary>What holds the keyboard when the list wants it.</summary>
public enum KeyHolder
{
    /// <summary>Anything else, nothing at all included.</summary>
    Other,

    /// <summary>The box of a name being renamed in place (TRAP-76).</summary>
    NameEdit,

    /// <summary>The quick filter box of this panel.</summary>
    OwnFilter,

    /// <summary>The quick filter box of the other panel.</summary>
    OtherFilter,
}

/// <summary>
/// Whether the file list may put the keyboard on its cursor. WPF-free, so the rule can be tested; the
/// control says who holds the keys (<see cref="KeyHolder"/>).
/// </summary>
public static class ListFocus
{
    public static bool MayTakeKeys(KeyHolder holder, FocusClaim claim, bool panelIsActive)
    {
        if (claim == FocusClaim.Follow && !panelIsActive)
        {
            return false;
        }

        return holder switch
        {
            KeyHolder.NameEdit => false,
            KeyHolder.OwnFilter => claim == FocusClaim.Hand,
            KeyHolder.OtherFilter => claim is FocusClaim.Hand or FocusClaim.Panel,
            _ => true,
        };
    }
}
