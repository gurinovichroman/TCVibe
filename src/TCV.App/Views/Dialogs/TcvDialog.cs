using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TCV.App.Views.Dialogs;

/// <summary>Base class for all TCV dialogs.
/// Automatically focuses the IsDefault button on open,
/// unless some other control (e.g. a TextBox) already grabbed focus.</summary>
public class TcvDialog : Window
{
    public TcvDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        // A dialog is a card floating over the work surface: one step lighter than the panels behind it,
        // with its inputs and lists inset darker than itself (docs/DESIGN.md).
        SetResourceReference(BackgroundProperty, "CardBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        Controls.TitleBarTheme.Follow(this);
    }

    private bool _isClosed;

    /// <summary>The window has been closed and cannot be shown again; true already in <see cref="Window.Closed"/>.</summary>
    protected bool IsClosed => _isClosed;

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        base.OnClosed(e);
    }

    /// <summary>
    /// Closes the dialog with <paramref name="result"/>, ignoring the call once the window is gone.
    /// A view-model that finishes an async operation (a clone, a commit) and only then asks to close
    /// can arrive after the user has already closed the window with the title-bar X — and assigning
    /// <see cref="Window.DialogResult"/> to a closed window throws, on the dispatcher, with nothing
    /// to catch it.
    /// </summary>
    protected void CloseWithResult(bool result)
    {
        if (_isClosed) return;

        DialogResult = result;
        Close();
    }

    /// <summary>
    /// The "pick a program" file dialog of the tool editors (user tools, the F4 editor), or null when
    /// cancelled. A window, so it belongs to the view; view models only ask for the path.
    /// </summary>
    protected string? PickProgram()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Localization.LocalizationManager.Get("UserTools_BrowseTitle"),
            Filter = Localization.LocalizationManager.Get("UserTools_BrowseFilter"),
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>
    /// Puts the caret in <paramref name="box"/> with its first <paramref name="length"/> characters
    /// selected (all by default), so typing replaces the value the dialog came with. Called from
    /// <c>Loaded</c>, where a bound <c>Text</c> is still empty (TRAP-89): the binding writes the value
    /// a moment later and that write drops the selection, so it is made again once the dispatcher has
    /// worked through what is queued ahead of it — and only if the caret is still at the start, where
    /// that write leaves it: a character typed ahead of the callback, or a click, has moved it, and
    /// selecting again would turn what was just typed into a selection the next key replaces.
    /// </summary>
    protected static void FocusAndSelect(TextBox box, int length = int.MaxValue)
    {
        box.Focus();
        SelectLeading(box, length);
        box.Dispatcher.InvokeAsync(() =>
        {
            if (box.SelectionStart == 0)
            {
                SelectLeading(box, length);
            }
        }, DispatcherPriority.Input);
    }

    private static void SelectLeading(TextBox box, int length) =>
        box.Select(0, Math.Min(length, box.Text.Length));

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        var focused = FocusManager.GetFocusedElement(this);
        if (focused is null || focused == this)
            FindDefaultButton()?.Focus();
    }

    private Button? FindDefaultButton()
    {
        return FindVisualDescendants<Button>(this).FirstOrDefault(b => b.IsDefault);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var grandchild in FindVisualDescendants<T>(child))
                yield return grandchild;
        }
    }
}
