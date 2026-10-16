using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class GitDiffDialog : TcvDialog
{
    public GitDiffDialog(GitDiffDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // The window opens first and the repository is read afterwards, off the UI thread.
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
