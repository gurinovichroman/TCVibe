using System.Windows.Input;

namespace TCV.App.Controls;

/// <summary>
/// Which keys the panel's quick filter box keeps while it has the focus. The box is a text field for its
/// text and caret, and the list for everything else: Enter opens the file the cursor found, F3 views it,
/// F5 copies it, as in Total Commander's quick search. WPF-free apart from the key types, so it can be tested.
/// </summary>
public static class QuickFilterKeys
{
    /// <summary>
    /// True when <paramref name="key"/> edits the filter text: a character with or without Shift (AltGr
    /// too, TRAP-07), Space, Backspace, Delete, Insert, the caret keys, Esc, and the text box's own Ctrl
    /// chords (select all, copy, cut, paste, undo, redo, word moves). False for the keys that work on the
    /// list — the function keys, Enter, Tab, ↑ ↓ and the page keys, and the other Ctrl and Alt chords.
    /// </summary>
    public static bool BelongsToBox(Key key, ModifierKeys modifiers)
    {
        var chord = modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
        if (chord == (ModifierKeys.Control | ModifierKeys.Alt))
        {
            return true;
        }

        if (key is (>= Key.F1 and <= Key.F24) or Key.Enter or Key.Tab
            or Key.Up or Key.Down or Key.PageUp or Key.PageDown)
        {
            return false;
        }

        if (chord == ModifierKeys.None)
        {
            return true;
        }

        if (chord == ModifierKeys.Control)
        {
            return (modifiers & ModifierKeys.Shift) != 0
                ? key is Key.Left or Key.Right or Key.Home or Key.End or Key.Z
                : key is Key.A or Key.C or Key.X or Key.V or Key.Z or Key.Y
                    or Key.Back or Key.Delete or Key.Insert
                    or Key.Left or Key.Right or Key.Home or Key.End;
        }

        return false;
    }
}
