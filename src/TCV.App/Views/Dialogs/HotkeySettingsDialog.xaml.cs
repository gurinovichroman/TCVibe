using System.Windows;
using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class HotkeySettingsDialog : TcvDialog
{
    private readonly HotkeySettingsDialogViewModel _vm;

    public HotkeySettingsDialog(HotkeySettingsDialogViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        // Save closes the window itself once it has succeeded; one click, not two.
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        // The list is long; finding a command starts with typing its name.
        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Captures the pressed combination instead of typing it in (Alt chords arrive as SystemKey).</summary>
    private void RecordBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Plain Tab / Shift+Tab and Esc keep their usual meaning, or a keyboard user who lands here
        // can neither leave the box nor cancel the dialog. Those keys can still be typed into the
        // gesture box above ("Tab", "Escape").
        var modifiers = Keyboard.Modifiers;
        if ((key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
            || (key == Key.Escape && modifiers == ModifierKeys.None))
        {
            return;
        }

        _vm.RecordKeyPress(key, modifiers);
        e.Handled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
