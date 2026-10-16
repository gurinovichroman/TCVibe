using System.Windows;
using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// The help window. Modeless on purpose: help is read next to the thing it explains, and its
/// "Run" button acts on the main window while the article stays open.
/// </summary>
public partial class HelpDialog : TcvDialog
{
    public HelpDialog(HelpDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, e) =>
        {
            // A new page starts at its top, not where the previous one was scrolled to.
            if (e.PropertyName is nameof(HelpDialogViewModel.Heading))
            {
                ContentScroll.ScrollToTop();
            }
        };
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A modeless window has no IsCancel button to close it.
        if (e.Key == Key.Escape)
        {
            if (DataContext is HelpDialogViewModel { Query.Length: > 0 } vm)
            {
                vm.Query = string.Empty;   // first Esc clears the search, the second closes
            }
            else
            {
                Close();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }
}
