using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// The command palette (F1): a fuzzy-searchable list of every palette command. Enter runs the
/// highlighted command; the caller reads <see cref="CommandPaletteViewModel.SelectedItem"/> when
/// <c>ShowDialog()</c> returns true and dispatches it through the normal command router.
/// </summary>
public partial class CommandPaletteDialog : TcvDialog
{
    private readonly CommandPaletteViewModel _vm;

    public CommandPaletteDialog(CommandPaletteViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveSelection(1);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.Up:
                _vm.MoveSelection(-1);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.PageDown:
                _vm.MoveSelection(10);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.PageUp:
                _vm.MoveSelection(-10);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (_vm.SelectedItem is null) return;
        DialogResult = true;   // setting DialogResult closes the modal dialog
    }

    private void ScrollToSelection()
    {
        if (_vm.SelectedItem is not null)
            ResultsList.ScrollIntoView(_vm.SelectedItem);
    }
}
