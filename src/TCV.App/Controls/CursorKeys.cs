using System.Windows.Input;
using TCV.App.Settings;

namespace TCV.App.Controls;

/// <summary>
/// Which cursor keys the file list takes from its views and moves the panel's cursor with itself
/// (<c>FileListViewModel.MoveCursorTo</c>), and which it leaves to the view's own control. A key the
/// control handles itself moves the control's selection, and with Shift held that selection replaced
/// every mark made with Insert. WPF-free apart from the key types, so it can be tested.
/// </summary>
public static class CursorKeys
{
    /// <summary>
    /// True when the file list handles <paramref name="key"/> itself. <paramref name="move"/> is where it
    /// takes the cursor, or null for a key that is only swallowed.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Home and End in every list view: the grid's own only jump between the cells of a row.
    /// Ctrl+Home and Ctrl+End stay with the commands (GoHome) and the lists.</item>
    /// <item>Brief and Thumbnails: the arrows and the page keys too. A list of names in columns or tiles
    /// in rows cannot step through them in two dimensions, and left to the list the keys move only its
    /// focus, off the cursor. Ctrl with them counts as the plain key.</item>
    /// <item>Details: the arrows and the page keys only with Shift — the plain ones stay with the grid,
    /// which scrolls and focuses its rows itself. Shift+Left and Shift+Right are swallowed: in the grid
    /// they extend its selection from an anchor of its own, and that selection would become the marks.</item>
    /// </list>
    /// Alt chords are commands, and the Tree view has its own keys.
    /// </remarks>
    public static bool TryTake(Key key, ViewMode view, ModifierKeys modifiers, out GridMove? move)
    {
        move = null;
        if (view == ViewMode.Tree || (modifiers & ModifierKeys.Alt) != 0)
        {
            return false;
        }

        var ctrl = (modifiers & ModifierKeys.Control) != 0;
        var shift = (modifiers & ModifierKeys.Shift) != 0;
        switch (key)
        {
            case Key.Home or Key.End:
                if (ctrl)
                {
                    return false;
                }

                move = key == Key.Home ? GridMove.First : GridMove.Last;
                return true;

            case Key.Left or Key.Right when view == ViewMode.Details:
                return shift;

            case Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown:
                if (view == ViewMode.Details && !shift)
                {
                    return false;
                }

                move = key switch
                {
                    Key.Left => GridMove.Left,
                    Key.Right => GridMove.Right,
                    Key.Up => GridMove.Up,
                    Key.Down => GridMove.Down,
                    Key.PageUp => GridMove.PageUp,
                    _ => GridMove.PageDown,
                };
                return true;

            default:
                return false;
        }
    }

    /// <summary>How a view lays out its entries for <see cref="WrapGrid.Move"/>: Brief in columns, the rest in rows.</summary>
    public static WrapFlow FlowOf(ViewMode view) => view == ViewMode.Brief ? WrapFlow.Columns : WrapFlow.Rows;
}
