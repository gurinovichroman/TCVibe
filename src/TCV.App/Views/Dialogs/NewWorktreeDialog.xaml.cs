using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class NewWorktreeDialog : TcvDialog
{
    public NewWorktreeDialog(NewWorktreeDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The task title is the only field meant to be typed; branch and path derive from it.
        Loaded += (_, _) => TaskBox.Focus();
    }
}
