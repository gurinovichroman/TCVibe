using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class GitWorktreeDialog : TcvDialog
{
    private readonly GitWorktreeDialogViewModel _viewModel;

    public GitWorktreeDialog(GitWorktreeDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // Listing walks the status of every worktree, so it runs after the window is up rather than
        // in the constructor — otherwise a large repository looks like a hang before anything shows.
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    /// <summary>Delete removes the highlighted worktree — the command asks for confirmation first.</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || !_viewModel.RemoveSelectedCommand.CanExecute(null)) return;

        e.Handled = true;
        _viewModel.RemoveSelectedCommand.Execute(null);
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.OpenSelectedCommand.CanExecute(null))
            _viewModel.OpenSelectedCommand.Execute(null);
    }
}
