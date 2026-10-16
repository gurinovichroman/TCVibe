using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class GitBranchDialog : TcvDialog
{
    private readonly GitBranchDialogViewModel _viewModel;

    public GitBranchDialog(GitBranchDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // Reading the branches (and which worktree holds each) happens after the window is up.
        Loaded += async (_, _) =>
        {
            await viewModel.RefreshAsync();
            BranchList.Focus();
            if (BranchList.SelectedItem is not null)
            {
                BranchList.ScrollIntoView(BranchList.SelectedItem);
                (BranchList.ItemContainerGenerator.ContainerFromItem(BranchList.SelectedItem) as System.Windows.Controls.ListBoxItem)
                    ?.Focus();
            }
        };
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SwitchCommand.CanExecute(null))
            _viewModel.SwitchCommand.Execute(null);
    }

    /// <summary>Delete on a highlighted branch asks to delete it (the command confirms first).</summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || !_viewModel.DeleteCommand.CanExecute(null)) return;

        e.Handled = true;
        _viewModel.DeleteCommand.Execute(null);
    }

    /// <summary>
    /// Enter in the new-branch box creates that branch. Left alone it would press the dialog's
    /// default button — Switch — which acts on whatever the list happens to have selected, i.e. the
    /// opposite of what someone who just typed a new name meant.
    /// </summary>
    private void OnNewBranchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (_viewModel.CreateCommand.CanExecute(null)) _viewModel.CreateCommand.Execute(null);
    }
}
