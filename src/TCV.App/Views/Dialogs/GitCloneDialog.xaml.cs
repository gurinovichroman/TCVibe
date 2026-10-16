using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class GitCloneDialog : TcvDialog
{
    public GitCloneDialog(GitCloneDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The token lives only in the PasswordBox until Clone is pressed.
        viewModel.SecretProvider = () => SecretBox.Password;

        // The X button bypasses the Cancel command, so without this a clone would keep writing into
        // a folder nobody is watching any more.
        Closing += (_, _) =>
        {
            if (viewModel.IsBusy) viewModel.CancelCommand.Execute(null);
        };

        Loaded += (_, _) => FocusAndSelect(UrlBox);
    }
}
