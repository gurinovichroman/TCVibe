using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class SemanticSearchDialog : TcvDialog
{
    private readonly SemanticSearchDialogViewModel _viewModel;

    public SemanticSearchDialog(SemanticSearchDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The query box, not the default button: this window opens to be typed into.
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void OnHitsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.GoToFileCommand.CanExecute(null))
            _viewModel.GoToFileCommand.Execute(null);
    }
}
